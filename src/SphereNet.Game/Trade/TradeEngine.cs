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
/// Each side has a virtual container — the client shows items in these containers
/// inside the trade gump. The containers must exist as real Items so ClassicUO's
/// world.Get(serial) can find them when processing the 0x6F packet.
/// </summary>
public sealed class SecureTrade
{
    private readonly Serial _sessionId;
    private readonly Character _initiator;
    private readonly Character _partner;
    private readonly Item _initiatorContainer;
    private readonly Item _partnerContainer;

    private bool _initiatorAccepted;
    private bool _partnerAccepted;
    private bool _isCompleted;

    public Serial SessionId => _sessionId;
    public Character Initiator => _initiator;
    public Character Partner => _partner;
    public Item InitiatorContainer => _initiatorContainer;
    public Item PartnerContainer => _partnerContainer;
    public bool InitiatorAccepted => _initiatorAccepted;
    public bool PartnerAccepted => _partnerAccepted;
    public bool IsCompleted => _isCompleted;

    public SecureTrade(Serial sessionId, Character initiator, Character partner,
        Item initiatorContainer, Item partnerContainer)
    {
        _sessionId = sessionId;
        _initiator = initiator;
        _partner = partner;
        _initiatorContainer = initiatorContainer;
        _partnerContainer = partnerContainer;
    }

    public bool IsParticipant(Character ch) => ch == _initiator || ch == _partner;

    public Item GetOwnContainer(Character ch) =>
        ch == _initiator ? _initiatorContainer : _partnerContainer;

    public Item GetPartnerContainer(Character ch) =>
        ch == _initiator ? _partnerContainer : _initiatorContainer;

    public Character GetPartner(Character ch) =>
        ch == _initiator ? _partner : _initiator;

    public bool ToggleAccept(Character from)
    {
        if (_isCompleted) return false;

        if (from == _initiator) _initiatorAccepted = !_initiatorAccepted;
        else if (from == _partner) _partnerAccepted = !_partnerAccepted;

        return _initiatorAccepted && _partnerAccepted;
    }

    public void ResetAcceptance()
    {
        _initiatorAccepted = false;
        _partnerAccepted = false;
    }

    public void Cancel()
    {
        if (_isCompleted) return;
        _isCompleted = true;
    }

    public void Complete()
    {
        if (_isCompleted) return;
        _isCompleted = true;
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
    /// True when an NPC name carries a merchant role keyword. Legacy packs
    /// ship some trade NPCs as NPC=BRAIN_HUMAN (e.g. c_h_vendor keeps
    /// brain_vendor commented out) and lean on speech scripts, so the
    /// engine widens vendor detection by name.
    /// </summary>
    public static bool HasVendorNameKeyword(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLowerInvariant();
        return lower.Contains("vendor") || lower.Contains("shopkeep") ||
               lower.Contains("merchant");
    }

    /// <summary>
    /// Single vendor-detection predicate shared by the dclick, context-menu,
    /// speech and trade-validation paths — they must agree, or a vendor that
    /// answers "buy" refuses the double-click (and vice versa).
    /// </summary>
    public static bool IsVendorLike(Character npc)
    {
        if (npc.IsPlayer) return false;
        if (npc.NpcBrain == Core.Enums.NpcBrainType.Vendor) return true;
        return npc.NpcBrain is Core.Enums.NpcBrainType.Human or Core.Enums.NpcBrainType.None
               && HasVendorNameKeyword(npc.Name);
    }

    /// <summary>
    /// Process a buy request from player to vendor.
    /// Returns total gold cost. Negative = insufficient gold.
    /// </summary>
    public static int ProcessBuy(Character player, Character vendor, IReadOnlyList<TradeEntry> items)
    {
        if (!IsVendorLike(vendor))
            return -1;
        // Also checked in the packet handler; repeated here so no other caller can
        // commit a transaction for a ghost.
        if (player.IsDead)
            return -1;
        if (World == null)
            return -1;

        // Resolve every buy entry against an ACTUAL item inside this
        // vendor's stock container (LAYER 26 / 27). The client sends back
        // the stock item's serial; validating containment here prevents a
        // crafted packet from "buying" an arbitrary world item by serial,
        // and lets us decrement the virtual stock from the real entry.
        var stockA = vendor.GetEquippedItem(Core.Enums.Layer.VendorStock);
        var stockB = vendor.GetEquippedItem(Core.Enums.Layer.VendorExtra);
        uint stockUidA = stockA?.Uid.Value ?? 0;
        uint stockUidB = stockB?.Uid.Value ?? 0;

        long totalCost = 0;
        var resolved = new List<(Item Stock, int Amount, int Price)>(items.Count);
        var seenSerials = new HashSet<uint>();
        foreach (var entry in items)
        {
            if (entry.Amount <= 0 || entry.Amount > 999) return -1;
            // A crafted packet may repeat the same stock row. Validating each
            // line independently would let the combined amount exceed stock.
            if (!seenSerials.Add(entry.ItemUid.Value)) return -1;

            var stockItem = World.FindItem(entry.ItemUid);
            if (stockItem == null || stockItem.IsDeleted)
                return -1;
            uint cont = stockItem.ContainedIn.Value;
            if (cont == 0 || (cont != stockUidA && cont != stockUidB))
                return -1; // not part of this vendor's stock
            if (stockItem.Amount < entry.Amount)
                return -1; // not enough in stock

            // Price from THIS stock entry's own PRICE tag — not a GetServerBuyPrice
            // lookup by BaseId, which returns the first same-BaseId stock item's
            // price. With two same-id entries at different prices the server could
            // otherwise charge a different price than the client's selected row.
            int serverPrice = (stockItem.TryGetTag("PRICE", out string? rowPrice)
                    && int.TryParse(rowPrice, out int rp) && rp > 0)
                ? rp
                : GetServerBuyPrice(vendor, stockItem.BaseId);
            if (serverPrice <= 0) return -1;
            totalCost += (long)serverPrice * entry.Amount;
            resolved.Add((stockItem, (int)entry.Amount, serverPrice));
        }
        if (totalCost > int.MaxValue)
            return -1;

        var backpack = player.Backpack;
        if (backpack == null)
            return -1;

        bool isStaff = player.PrivLevel >= Core.Enums.PrivLevel.GM;
        // A character name is chosen by the player, so it can never be an economic
        // privilege: "SphereBotanist" is a perfectly ordinary name, and it used to buy
        // for free. The exemption now requires the server to actually be driving this
        // character as a load-test bot.
        bool isBot = Diagnostics.BotEngine.IsLiveBotCharacter(player.Name);
        bool isOwner = vendor.HasOwner(player.Uid);
        if (!isStaff && !isBot && !isOwner)
        {
            long playerGold = CountGold(player);
            if (playerGold < totalCost)
                return -1;

            RemoveGold(player, (int)totalCost);
        }
        else
        {
            totalCost = 0;
        }

        // Credit the vendor's money pool with what the player paid (Source-X
        // pVendor->GetBank()->m_itEqBankBox.m_Check_Amount += iCostTotal).
        if (totalCost > 0)
        {
            long currentPurse = GetVendorGold(vendor);
            SetVendorGold(vendor, currentPurse > long.MaxValue - totalCost
                ? long.MaxValue
                : currentPurse + totalCost);
        }

        foreach (var (stock, amount, _) in resolved)
        {
            // Materialise the purchased item only now, on buy. Full-clone the stock
            // entry (Source-X CreateDupeItem) so per-instance state — tags, durability,
            // price, more-fields — travels with it, not just id/hue/name.
            var newItem = World.CreateItem();
            newItem.CopyStackInstanceStateFrom(stock);
            newItem.Amount = (ushort)Math.Max(1, Math.Min(amount, ushort.MaxValue));
            Item? delivered = player.PrivLevel < Core.Enums.PrivLevel.GM && !player.CanCarry(newItem)
                ? null
                : backpack.TryAddItemWithStack(newItem);
            if (delivered == null)
            {
                // Source-X ItemBounce drops at the buyer's feet when the pack is
                // full or the purchase would overload them; never orphan a paid item.
                World.PlaceItemWithDecay(newItem, player.Position);
            }
            else if (delivered != newItem)
            {
                // The clone merged into an existing pile. Remove the transient
                // world object that supplied the amount.
                World.RemoveItem(newItem);
            }

            // Decrement the virtual stock; a depleted entry is removed.
            // When the whole container empties it is rebuilt from the
            // SELL template the next time the vendor is opened.
            if (stock.Amount <= amount)
                World.RemoveItem(stock);
            else
                stock.Amount -= (ushort)amount;
        }

        return (int)totalCost;
    }

    /// <summary>
    /// Process a sell request from player to vendor.
    /// Returns total gold earned.
    /// </summary>
    public static int ProcessSell(Character player, Character vendor, IReadOnlyList<TradeEntry> items) =>
        ProcessSell(player, vendor, items, out _);

    /// <summary>
    /// Sell with shortfall reporting: Source-X fills line by line and BREAKS
    /// with a shortfall flag when the vendor's purse runs out — a partial fill,
    /// not all-or-nothing. <paramref name="shortfall"/> lets the caller bark
    /// the "I have no money" line.
    /// </summary>
    public static int ProcessSell(Character player, Character vendor, IReadOnlyList<TradeEntry> items, out bool shortfall)
    {
        shortfall = false;
        if (player.IsDead)
            return -1;
        if (!IsVendorLike(vendor))
            return 0;

        // Source-X NPC_FindVendableItem: a vendor with a BUY list only buys items on
        // it. No list = buys anything (legacy behaviour preserved). Authoritative —
        // rejects a crafted packet that submits an item the vendor wouldn't list.
        var buyFilter = GetVendorBuyFilter(vendor);

        long totalValue = 0;
        var validated = new List<(TradeEntry Entry, Item Item, int ServerPrice)>();
        var seenSerials = new HashSet<uint>();
        foreach (var entry in items)
        {
            if (entry.Amount <= 0 || entry.Amount > ushort.MaxValue)
                return 0;
            if (!seenSerials.Add(entry.ItemUid.Value))
                return 0;

            var found = FindItemInBackpack(player, entry.ItemUid);
            if (found == null || found.IsDeleted || found.Amount < entry.Amount ||
                found.ItemType == Core.Enums.ItemType.Gold || found.BaseId == 0x0EED)
                return 0;
            // A vendor never buys an immovable item, nor a NON-EMPTY container: the
            // sale deletes the item, which would silently destroy a bag's contents
            // for the bag's price (item-loss exploit, worse on a no-buy-list vendor
            // that buys anything). Mirrors ServUO BaseVendor's sell guard
            // (skip !Movable and (Container && Items.Count != 0)).
            if (found.IsAttr(Core.Enums.ObjAttributes.Move_Never) ||
                (found.ItemType == Core.Enums.ItemType.Container && found.Contents.Count > 0))
                return 0;
            if (buyFilter != null && !buyFilter.Contains(found.BaseId))
                return 0; // vendor does not buy this item type

            int serverPrice = GetServerSellPrice(vendor, found);
            if (serverPrice <= 0) return 0;
            totalValue += (long)serverPrice * entry.Amount;
            if (totalValue > int.MaxValue)
                return 0;

            validated.Add((entry, found, serverPrice));
        }

        // Source-X fills line by line against the vendor's purse and BREAKS
        // with a shortfall when the next line can't be paid — earlier lines
        // still complete (partial fill).
        long purse = GetVendorGold(vendor);
        long payout = 0;
        var affordable = new List<(TradeEntry Entry, Item Item, int ServerPrice)>(validated.Count);
        foreach (var line in validated)
        {
            long linePrice = (long)line.ServerPrice * line.Entry.Amount;
            if (payout + linePrice > purse)
            {
                shortfall = true;
                break;
            }
            payout += linePrice;
            affordable.Add(line);
        }
        if (affordable.Count == 0)
            return 0;

        if (World != null)
        {
            var backpack = player.Backpack;
            if (backpack == null)
                return 0;

            foreach (var (entry, found, _) in affordable)
            {
                if (found.Amount <= entry.Amount)
                    World.RemoveItem(found);
                else
                    found.Amount -= (ushort)entry.Amount;
            }

            // Debit the vendor's purse by what was actually paid out.
            SetVendorGold(vendor, purse - payout);

            // Add gold to player (split into 60000-max piles)
            GiveGoldToPack(player, (int)payout);
        }

        return (int)payout;
    }

    // ---- Vendor money pool (Source-X m_Check_Amount): ALWAYS tracked. Buying
    // credits the vendor's purse, selling debits it; the purse is topped up to
    // RestockGold at every restock, so a freshly opened vendor can buy. A
    // vendor with no VENDOR_GOLD tag simply has an empty purse until then. ----

    /// <summary>Gold the vendor purse is topped up to at each restock.</summary>
    public static int RestockGold { get; set; } = 2000;

    /// <summary>Source-X always tracks vendor funds; kept for API compatibility.</summary>
    public static bool VendorTracksMoney(Character vendor) =>
        vendor.TryGetTag("VENDOR_GOLD", out string? s) && !string.IsNullOrWhiteSpace(s);

    public static long GetVendorGold(Character vendor) =>
        vendor.TryGetTag("VENDOR_GOLD", out string? s) && long.TryParse(s, out long g)
            ? Math.Max(0, g) : 0;

    private static void SetVendorGold(Character vendor, long amount) =>
        vendor.SetTag("VENDOR_GOLD", Math.Max(0, amount).ToString());

    /// <summary>Source-X NPC_VendorGetChkVerb PC_CASH: hand the whole vendor purse
    /// to <paramref name="owner"/> and zero it. Only real earnings accumulate on
    /// an owned vendor (restock never tops up an owned purse), so this cannot
    /// mint gold. Returns the amount dispensed (0 if the purse was empty).</summary>
    public static int DispenseVendorGold(Character vendor, Character owner)
    {
        int amount = (int)GetVendorGold(vendor);
        if (amount <= 0) return 0;
        SetVendorGold(vendor, 0);
        GiveGoldToPack(owner, amount);
        return amount;
    }

    /// <summary>Deliver <paramref name="amount"/> gold to a character's pack,
    /// split into piles (Source-X CItem::MakeGold), dropping overflow at the feet.</summary>
    public static void GiveGoldToPack(Character ch, int amount)
    {
        if (World == null || amount <= 0) return;
        var backpack = ch.Backpack;
        int remaining = amount;
        while (remaining > 0 && backpack != null)
        {
            int pile = Math.Min(remaining, 60000);
            var gold = World.CreateItem();
            gold.BaseId = 0x0EED;
            gold.ItemType = Core.Enums.ItemType.Gold;
            gold.Amount = (ushort)pile;
            gold.Name = "Gold";
            Item? delivered = ch.PrivLevel < Core.Enums.PrivLevel.GM && !ch.CanCarry(gold)
                ? null
                : backpack.TryAddItemWithStack(gold);
            if (delivered == null)
                World.PlaceItemWithDecay(gold, ch.Position);
            else if (delivered != gold)
                World.RemoveItem(gold);
            remaining -= pile;
        }
    }

    /// <summary>The set of item BaseIds a vendor will buy (Source-X NPC_FindVendableItem),
    /// resolved from its VENDOR_BUY_LIST template, or null when the vendor has no buy
    /// list — in which case it buys anything (the legacy behaviour is preserved). An
    /// unresolvable template also yields null so selling is never silently broken.</summary>
    public static HashSet<ushort>? GetVendorBuyFilter(Character vendor)
    {
        if (!vendor.TryGetTag("VENDOR_BUY_LIST", out string? tpl) || string.IsNullOrWhiteSpace(tpl))
            return null;

        var set = new HashSet<ushort>();
        var resources = SphereNet.Game.Definitions.DefinitionLoader.StaticResources;
        foreach (var (defName, _) in SphereNet.Game.Definitions.TemplateEngine.EnumerateSequential(tpl!))
        {
            ushort id = resources != null
                ? SphereNet.Game.Definitions.TemplateEngine.ResolveDispId(resources, defName)
                : (ushort)0;
            if (id != 0) set.Add(id);
        }
        // A configured but empty/malformed list means "buys nothing". Returning
        // null here would turn a script typo into an unrestricted buy-anything vendor.
        return set;
    }

    /// <summary>Look up server-side price for an item. Checks vendor stock PRICE tags, item def, fallback to baseId formula.</summary>
    internal static int GetServerBuyPrice(Character vendor, ushort itemId)
    {
        var stock = vendor.GetEquippedItem(Core.Enums.Layer.VendorStock);
        if (stock != null)
        {
            foreach (var item in stock.Contents)
            {
                if (item.BaseId == itemId &&
                    item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int p))
                    return Math.Max(1, p);
            }
        }
        var pack = vendor.Backpack;
        if (pack != null)
        {
            foreach (var item in pack.Contents)
            {
                if (item.BaseId == itemId &&
                    item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int p))
                    return Math.Max(1, p);
            }
        }
        // No PRICE tag — price from the itemdef VALUE like Source-X
        // (CItemVendable::GetMakeValue). The old fallback derived the price
        // from the ART TILE ID (/10 + 5), so high-graphic items cost a fortune.
        return Math.Max(1, GetDefValue(itemId));
    }

    /// <summary>Itemdef VALUE midpoint — the Source-X vendor pricing base.
    /// Returns 0 when the def declares no VALUE.</summary>
    internal static int GetDefValue(ushort itemId)
    {
        var idef = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(itemId);
        if (idef == null) return 0;
        return idef.ValueMin > 0 && idef.ValueMax > 0
            ? (idef.ValueMin + idef.ValueMax) / 2
            : Math.Max(idef.ValueMin, idef.ValueMax);
    }

    /// <summary>Default VENDORMARKUP percent (Source-X g_Cfg.m_iVendorMarkup).</summary>
    public static int DefaultVendorMarkup { get; set; } = 15;

    /// <summary>Source-X NPC_GetVendorMarkup: vendor tag → region tag → config
    /// default. The markup is the vendor's profit margin percent.</summary>
    public static int GetVendorMarkup(Character vendor)
    {
        if (vendor.TryGetTag("VENDORMARKUP", out string? v) && int.TryParse(v, out int mv))
            return Math.Clamp(mv, 0, 99);
        var region = World?.FindRegion(vendor.Position);
        if (region != null && region.TryGetTag("VENDORMARKUP", out string? rv) &&
            int.TryParse(rv, out int rmv))
            return Math.Clamp(rmv, 0, 99);
        return Math.Clamp(DefaultVendorMarkup, 0, 99);
    }

    /// <summary>Server-side sell price (what the vendor pays the player).
    /// The stock PRICE is the buy (marked-up) price, so the payout works the
    /// markup out of it twice: base = price/(1+m), payout = base*(1-m) —
    /// Source-X GetVendorPrice with iConvertFactor = -markup. The old fixed
    /// buy/2 ignored VENDORMARKUP entirely.</summary>
    internal static int GetServerSellPrice(Character vendor, Item item)
    {
        int buyPrice = item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int p)
            ? Math.Max(1, p)
            : Math.Max(1, GetServerBuyPrice(vendor, item.BaseId));
        int markup = GetVendorMarkup(vendor);
        return Math.Max(1, buyPrice * (100 - markup) / (100 + markup));
    }

    /// <summary>Count gold in player's backpack recursively.</summary>
    public static long CountGold(Character ch)
    {
        if (World == null) return 0;
        var backpack = ch.Backpack;
        if (backpack == null) return 0;

        long total = 0;
        foreach (var item in EnumerateContainerContentsRecursive(backpack))
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
        foreach (var item in EnumerateContainerContentsRecursive(backpack).ToList())
        {
            if (remaining <= 0) break;
            if (item.ItemType != Core.Enums.ItemType.Gold && item.BaseId != 0x0EED)
                continue;

            if (item.Amount <= remaining)
            {
                remaining -= item.Amount;
                World.RemoveItem(item);
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
        var backpack = ch.Backpack;
        if (backpack == null) return null;
        return EnumerateContainerContentsRecursive(backpack)
            .FirstOrDefault(item => item.Uid == itemUid);
    }

    private static IEnumerable<Item> EnumerateContainerContentsRecursive(Item container)
    {
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Item>(container.Contents.Reverse());
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (item.IsDeleted)
                continue;
            if (!seen.Add(item))
                continue;

            yield return item;
            for (int i = item.Contents.Count - 1; i >= 0; i--)
                pending.Push(item.Contents[i]);
        }
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
        if (!IsVendorLike(vendor)) return;

        // Restock refills the vendor's purse first (Source-X: the vendor bank
        // is the buy fund) — even for vendors with no VENDORINV stock list, so
        // a buy-only vendor can still purchase from players. This top-up is the
        // shopkeeper's infinite buy fund; an OWNED vendor (player/pet vendor)
        // must NOT be topped up, or dispensing its purse to the owner (CASH)
        // would be a free-gold faucet. Owned vendors keep only real earnings.
        if (!vendor.OwnerSerial.IsValid && GetVendorGold(vendor) < RestockGold)
            SetVendorGold(vendor, RestockGold);

        if (!vendor.TryGetTag("VENDORINV", out string? invDef) || string.IsNullOrEmpty(invDef))
            return;

        // Stock the dedicated vendor STOCK container (LAYER 26), the same
        // container the buy gump reads from — not the regular backpack,
        // which the display path ignores. The container and its contents
        // are virtual (excluded from the world save, rebuilt on demand).
        var stock = vendor.GetEquippedItem(Core.Enums.Layer.VendorStock);
        if (stock == null)
        {
            stock = World.CreateItem();
            stock.BaseId = 0x408D; // i_vendor_box (vendor stock graphic)
            vendor.Equip(stock, Core.Enums.Layer.VendorStock);
        }

        // Parse "itemId:amount,itemId:amount,..."
        var entries = invDef.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var existing = new Dictionary<ushort, int>();

        // Count existing stock
        foreach (var item in World.GetContainerContents(stock.Uid))
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

            // Stamp the buy price from the itemdef VALUE when available so
            // the server-side price check has an explicit figure (it falls
            // back to the display formula otherwise).
            var idef = Definitions.DefinitionLoader.GetItemDef(itemId);
            if (idef != null)
            {
                int value = idef.ValueMin > 0 && idef.ValueMax > 0
                    ? (idef.ValueMin + idef.ValueMax) / 2
                    : Math.Max(idef.ValueMin, idef.ValueMax);
                if (value > 0)
                {
                    newItem.Price = value;
                    newItem.SetTag("PRICE", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            if (!stock.TryAddItem(newItem))
                World.RemoveItem(newItem);
        }

        vendor.SetTag("RESTOCK_TIME", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
    }

    /// <summary>Check if vendor needs restocking (based on RESTOCK_TIME tag).</summary>
    public static bool NeedsRestock(Character vendor, int intervalMs = DefaultRestockInterval)
    {
        if (!vendor.TryGetTag("RESTOCK_TIME", out string? timeStr) || !long.TryParse(timeStr, out long lastRestock))
            return true;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastRestock >= intervalMs;
    }
}

/// <summary>
/// Manages active trade sessions.
/// </summary>
public sealed class TradeManager
{
    private readonly Dictionary<Serial, SecureTrade> _activeTrades = [];
    private readonly Dictionary<uint, SecureTrade> _containerIndex = [];
    private uint _nextSessionId;

    public SecureTrade? GetTrade(Serial sessionId) =>
        _activeTrades.GetValueOrDefault(sessionId);

    public SecureTrade? FindByContainer(uint containerSerial) =>
        _containerIndex.GetValueOrDefault(containerSerial);

    public SecureTrade StartTrade(Character initiator, Character partner,
        Item initiatorContainer, Item partnerContainer)
    {
        var sessionId = new Serial(++_nextSessionId | 0x80000000);
        var trade = new SecureTrade(sessionId, initiator, partner,
            initiatorContainer, partnerContainer);
        _activeTrades[sessionId] = trade;
        _containerIndex[initiatorContainer.Uid.Value] = trade;
        _containerIndex[partnerContainer.Uid.Value] = trade;
        return trade;
    }

    public void EndTrade(SecureTrade trade)
    {
        if (!trade.IsCompleted)
            trade.Cancel();
        _activeTrades.Remove(trade.SessionId);
        _containerIndex.Remove(trade.InitiatorContainer.Uid.Value);
        _containerIndex.Remove(trade.PartnerContainer.Uid.Value);
    }

    public SecureTrade? FindTradeFor(Character ch) =>
        _activeTrades.Values.FirstOrDefault(t =>
            !t.IsCompleted && (t.Initiator == ch || t.Partner == ch));

    /// <summary>Return all trade-container items to their owners' backpacks.</summary>
    public static void ReturnTradeItems(GameWorld world, SecureTrade trade)
    {
        ReturnContainerItems(world, trade.Initiator, trade.InitiatorContainer);
        ReturnContainerItems(world, trade.Partner, trade.PartnerContainer);
    }

    private static void ReturnContainerItems(GameWorld world, Character owner, Item container)
    {
        foreach (var item in container.Contents.ToList())
        {
            container.RemoveItem(item);
            ReturnItemToCharacter(world, owner, item);
        }
    }

    /// <summary>Move one item into the character backpack (or feet if no pack).</summary>
    public static void ReturnItemToCharacter(GameWorld world, Character owner, Item item)
    {
        var pack = EnsureBackpack(world, owner);
        if (pack != null && pack.TryAddItem(item))
        {
            item.Position = new Point3D(0, 0, 0, owner.MapIndex);
            return;
        }

        // ItemBounce semantics: a full/missing pack never destroys or strands
        // returned trade goods; they land at the owner's feet.
        world.PlaceItemWithDecay(item, owner.Position);
    }

    private static Item? EnsureBackpack(GameWorld world, Character ch)
    {
        if (!ch.IsPlayer)
            return ch.Backpack;

        Item? pack = ch.GetEquippedItem(Core.Enums.Layer.Pack) ?? ch.Backpack;
        if (pack == null || pack.IsDeleted || world.FindItem(pack.Uid) == null)
        {
            pack = world.CreateItem();
            pack.BaseId = 0x0E75;
            pack.ItemType = Core.Enums.ItemType.Container;
            pack.Name = "Backpack";
        }

        ch.Backpack = pack;
        ch.Equip(pack, Core.Enums.Layer.Pack);
        return pack;
    }

    /// <summary>Sum incoming trade weight against recipient carry capacity.</summary>
    public static bool CanAcceptTradeItems(Character recipient, GameWorld world, Item sourceContainer,
        out string? reason)
    {
        reason = null;
        if (recipient.IsDeleted || sourceContainer.IsDeleted)
        {
            reason = "Trade is no longer valid.";
            return false;
        }

        int incomingSlots = sourceContainer.Contents.Count(i => !i.IsDeleted);
        if (incomingSlots == 0)
            return true;

        var pack = EnsureBackpack(world, recipient);
        if (pack == null && recipient.IsPlayer)
        {
            reason = "You have no backpack.";
            return false;
        }

        if (pack != null && pack.ContentCount + incomingSlots > Item.MaxContainerItems)
        {
            reason = "Your backpack cannot hold any more items.";
            return false;
        }

        long maxWeightTenths = (long)Math.Max(0, recipient.MaxWeight) * Item.WeightUnits;
        long incomingTenths = 0;
        foreach (var item in sourceContainer.Contents)
        {
            if (item.IsDeleted) continue;
            incomingTenths += item.TotalWeightTenths;
            if (incomingTenths > int.MaxValue)
                break;
        }

        if ((long)recipient.GetTotalWeightTenths() + incomingTenths > maxWeightTenths)
        {
            reason = "You cannot carry that much.";
            return false;
        }

        return true;
    }
}
