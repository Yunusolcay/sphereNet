using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>World-features handler (decomposition phase 3) — the members
    /// below delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientWorldFeaturesHandler"/>.</summary>
    internal ClientWorldFeaturesHandler WorldFeatures => _worldFeatures ??= new ClientWorldFeaturesHandler(this);
    private ClientWorldFeaturesHandler? _worldFeatures;

    public void OpenCraftingGump(SkillType craftSkill) => WorldFeatures.OpenCraftingGump(craftSkill);

    public void HandleVendorBuy(uint vendorSerial, byte flag,
        List<SphereNet.Network.Packets.Incoming.VendorBuyEntry> buyItems) =>
        WorldFeatures.HandleVendorBuy(vendorSerial, flag, buyItems);

    public void HandleVendorSell(uint vendorSerial,
        List<SphereNet.Network.Packets.Incoming.VendorSellEntry> sellItems) =>
        WorldFeatures.HandleVendorSell(vendorSerial, sellItems);

    internal static int GetVendorItemPrice(Character vendor, Item item) =>
        ClientWorldFeaturesHandler.GetVendorItemPrice(vendor, item);

    internal static int GetVendorItemSellPrice(Character vendor, Item item) =>
        ClientWorldFeaturesHandler.GetVendorItemSellPrice(vendor, item);

    public void HandleSecureTrade(byte action, uint containerSerial, uint param) =>
        WorldFeatures.HandleSecureTrade(action, containerSerial, param);

    internal void AbortActiveTradeOnDisconnect() => WorldFeatures.AbortActiveTradeOnDisconnect();

    /// <summary>Cancel an open secure trade because this character died
    /// (Source-X CChar::Death Trade_Delete).</summary>
    public void CancelActiveTradeOnDeath() => WorldFeatures.CancelActiveTradeOnDeath();

    public bool InitiateTrade(Character partner, Item? firstItem = null) =>
        WorldFeatures.InitiateTrade(partner, firstItem);

    internal void SendTradeUpdateToBoth(SecureTrade trade) => WorldFeatures.SendTradeUpdateToBoth(trade);

    // Partner-side trade callbacks wired from Program.cs — they stay on
    // GameClient so the server wiring surface is unchanged; the handler
    // reads them through shims.
    public Action<Character, Character, Item, Item>? SendTradeToPartner { get; set; }
    public Action<Character, Item, Item>? SendTradeItemToPartner { get; set; }
    public Action<Character, uint>? SendTradeCloseToPartner { get; set; }
    public Action<Character, SecureTrade>? SendTradeUpdateToPartner { get; set; }
    public Action<Character, string>? SendTradeMessageToPartner { get; set; }
    public Action<Character>? RefreshBackpackForPartner { get; set; }

    public void HandleRename(uint serial, string name) => WorldFeatures.HandleRename(serial, name);

    public void HandleViewRange(byte range) => WorldFeatures.HandleViewRange(range);

    internal void OpenGuildStoneGump(Item stone) => WorldFeatures.OpenGuildStoneGump(stone);

    internal void OpenHouseSignGump(Item signOrMulti) => WorldFeatures.OpenHouseSignGump(signOrMulti);

    public void OpenDoor() => WorldFeatures.OpenDoor();

    internal bool TryToggleNearestMapStaticDoor(uint clientSerial) =>
        WorldFeatures.TryToggleNearestMapStaticDoor(clientSerial);

    internal void ToggleDoor(Item door) => WorldFeatures.ToggleDoor(door);

    internal void UsePotion(Item potion) => WorldFeatures.UsePotion(potion);

    public void HandleUseSkill(int skillId) => WorldFeatures.HandleUseSkill(skillId);

    public void HandleExtendedCommand(ushort subCmd, byte[] data) =>
        WorldFeatures.HandleExtendedCommand(subCmd, data);

    public void HandleVirtueInvoke(int virtueId) => WorldFeatures.HandleVirtueInvoke(virtueId);

    public void HandleCrashReport() => WorldFeatures.HandleCrashReport();

    /// <summary>Advance the pending multi-stroke craft (tick pump).</summary>
    internal void TickPendingCraft() => WorldFeatures.TickPendingCraft();
    public void CancelPendingCraftOnInterrupt() => WorldFeatures.CancelPendingCraftOnInterrupt();

    internal bool BeginPendingCraft(Crafting.CraftRecipe recipe, Core.Enums.SkillType craftSkill, bool reopenGump) =>
        WorldFeatures.BeginPendingCraft(recipe, craftSkill, reopenGump);

    public void HandleClientUiButton(byte opcode) => WorldFeatures.HandleClientUiButton(opcode);

    // Test-harness reflection entry points (GameSystemTests InvokePrivate).
    internal void SendContextMenu(uint targetSerial) => WorldFeatures.SendContextMenu(targetSerial);

    internal void HandleContextMenuResponse(uint targetSerial, ushort entryTag) =>
        WorldFeatures.HandleContextMenuResponse(targetSerial, entryTag);
}
