using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{

    /// <summary>
    /// Open a crafting gump for the given skill.
    /// Lists available recipes and lets the player select one to craft.
    /// </summary>
    public void OpenCraftingGump(SkillType craftSkill)
    {
        if (_character == null || _craftingEngine == null) return;

        var recipes = _craftingEngine.GetRecipesBySkill(craftSkill);
        if (recipes.Count == 0)
        {
            SysMessage(ServerMessages.Get("craft_no_recipes"));
            return;
        }

        var gump = new GumpBuilder(_character.Uid.Value, 0, 530, 437);
        gump.AddResizePic(0, 0, 5054, 530, 437);
        gump.AddText(15, 15, 0, $"{craftSkill} Menu");

        // Page 0 — recipe list
        int y = 50;
        int buttonId = 100;
        foreach (var recipe in recipes)
        {
            if (y > 390) break;

            string name = string.IsNullOrEmpty(recipe.ResultName)
                ? $"Item 0x{recipe.ResultItemId:X4}"
                : recipe.ResultName;
            bool canMake = _craftingEngine.CanCraft(_character, recipe);
            int hue = canMake ? 0x0044 : 0x0020; // green vs red

            gump.AddButton(15, y, 4005, 4007, buttonId);
            gump.AddText(55, y, hue, name);

            // Show resource info
            if (recipe.Resources.Count > 0)
            {
                var resText = string.Join(", ", recipe.Resources.Select(r => $"{r.Amount}x 0x{r.ItemId:X4}"));
                gump.AddText(280, y, 0, resText);
            }

            y += 22;
            buttonId++;
        }

        // Cancel button
        gump.AddButton(15, 400, 4017, 4019, 0);
        gump.AddText(55, 400, 0, "Close");

        SendGump(gump, (pressedButton, switches, textEntries) =>
        {
            if (pressedButton >= 100)
            {
                int index = (int)(pressedButton - 100);
                if (index < recipes.Count)
                {
                    var recipe = recipes[index];

                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillMakeItem,
                        new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill });

                    var result = _craftingEngine.TryCraft(_character, recipe);

                    if (result != null)
                    {
                        var pack = _character.Backpack;
                        if (pack != null)
                        {
                            var actual = pack.AddItemWithStack(result);
                            if (actual != result)
                                result.Delete();

                            _netState.Send(new PacketContainerItem(
                                actual.Uid.Value, actual.DispIdFull, 0,
                                actual.Amount, actual.X, actual.Y,
                                pack.Uid.Value, actual.Hue,
                                _netState.IsClientPost6017));

                            _triggerDispatcher?.FireItemTrigger(actual, ItemTrigger.Create,
                                new TriggerArgs { CharSrc = _character, ItemSrc = actual });
                        }
                        else
                        {
                            _world.PlaceItemWithDecay(result, _character.Position);
                            _triggerDispatcher?.FireItemTrigger(result, ItemTrigger.Create,
                                new TriggerArgs { CharSrc = _character, ItemSrc = result });
                        }
                        SysMessage(ServerMessages.GetFormatted("craft_success", result.GetName()));
                    }
                    else
                        SysMessage(ServerMessages.Get("craft_fail"));

                    // Re-open gump for continued crafting
                    OpenCraftingGump(craftSkill);
                }
            }
        });
    }

    /// <summary>Handle vendor buy packet (0x3B).</summary>
    public void HandleVendorBuy(uint vendorSerial, byte flag,
        List<SphereNet.Network.Packets.Incoming.VendorBuyEntry> buyItems)
    {
        if (_character == null) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || vendor.NpcBrain != NpcBrainType.Vendor) return;

        if (flag == 0 || buyItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // Fire @Buy trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "BUY" });

        // Build trade entries from packet data
        var entries = new List<TradeEntry>();
        foreach (var bi in buyItems)
        {
            var item = _world.FindItem(new Serial(bi.ItemSerial));
            if (item == null) continue;

            int price = GetVendorItemPrice(vendor, item);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = bi.Amount
            });
        }

        int result = VendorEngine.ProcessBuy(_character, vendor, entries);
        if (result < 0)
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nomoney1"));
        else if (result == 0)
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
        else
            NpcSpeech(vendor, ServerMessages.GetFormatted("npc_vendor_b1", result, result == 1 ? "" : "s"));

        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>Handle vendor sell packet (0x9F).</summary>
    public void HandleVendorSell(uint vendorSerial,
        List<SphereNet.Network.Packets.Incoming.VendorSellEntry> sellItems)
    {
        if (_character == null) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || vendor.NpcBrain != NpcBrainType.Vendor) return;

        if (sellItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // Fire @Sell trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "SELL" });

        // Build trade entries from packet data
        var entries = new List<TradeEntry>();
        foreach (var si in sellItems)
        {
            var item = _world.FindItem(new Serial(si.ItemSerial));
            if (item == null) continue;

            int price = GetVendorItemSellPrice(vendor, item);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = si.Amount
            });
        }

        int result = VendorEngine.ProcessSell(_character, vendor, entries);
        NpcSpeech(vendor, ServerMessages.GetFormatted("npc_vendor_sell_ty", result, result == 1 ? "" : "s"));
        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>Get the buy price for an item from vendor inventory. Uses TAG.PRICE or defaults.</summary>
    private static int GetVendorItemPrice(Character vendor, Item item)
    {
        if (item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int price))
            return price;
        return Math.Max(1, item.BaseId / 10 + 5); // default price
    }

    /// <summary>Get the sell price (what vendor pays the player). Usually half of buy price.</summary>
    private static int GetVendorItemSellPrice(Character vendor, Item item)
    {
        return Math.Max(1, GetVendorItemPrice(vendor, item) / 2);
    }

    /// <summary>
    /// Handle secure trade packet (0x6F).
    /// Actions: 0=display, 1=close, 2=update (check/uncheck accept).
    /// </summary>
    public void HandleSecureTrade(byte action, uint containerSerial, uint param)
    {
        if (_character == null || _tradeManager == null) return;

        var trade = _tradeManager.FindByContainer(containerSerial);
        if (trade == null) return;

        switch (action)
        {
            case 1: // Cancel
                CancelTrade(trade);
                break;
            case 2: // Accept toggle
            {
                bool bothAccepted = trade.ToggleAccept(_character);
                SendTradeUpdateToBoth(trade);

                if (bothAccepted)
                    CompleteTrade(trade);
                break;
            }
        }
    }

    public void InitiateTrade(Character partner, Item? firstItem = null)
    {
        if (_character == null || _tradeManager == null) return;

        var existing = _tradeManager.FindTradeFor(_character);
        if (existing != null) { SysMessage("You are already trading."); return; }

        var partnerTrade = _tradeManager.FindTradeFor(partner);
        if (partnerTrade != null) { SysMessage("They are already trading."); return; }

        var cont1 = _world.CreateItem();
        cont1.BaseId = 0x1E5E;
        cont1.ItemType = Core.Enums.ItemType.Container;
        cont1.Name = "Trade Container";

        var cont2 = _world.CreateItem();
        cont2.BaseId = 0x1E5E;
        cont2.ItemType = Core.Enums.ItemType.Container;
        cont2.Name = "Trade Container";

        var trade = _tradeManager.StartTrade(_character, partner, cont1, cont2);

        _netState.Send(new PacketWorldItem(cont1.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(new PacketWorldItem(cont2.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(new PacketSecureTradeOpen(
            partner.Uid.Value, cont1.Uid.Value, cont2.Uid.Value, partner.GetName()));

        SendTradeToPartner?.Invoke(partner, _character, cont1, cont2);

        if (firstItem != null)
        {
            cont1.AddItem(firstItem);
            _netState.Send(new PacketContainerItem(
                firstItem.Uid.Value, firstItem.DispIdFull, 0,
                firstItem.Amount, 30, 30,
                cont1.Uid.Value, firstItem.Hue, _netState.IsClientPost6017));
            SendTradeItemToPartner?.Invoke(partner, firstItem, cont1);
        }
    }

    private void CancelTrade(SecureTrade trade)
    {
        var partner = trade.GetPartner(_character!);
        var myCont = trade.GetOwnContainer(_character!);
        var theirCont = trade.GetPartnerContainer(_character!);

        foreach (var item in _world.GetContainerContents(myCont.Uid).ToList())
            PlaceItemInPack(_character!, item);
        foreach (var item in _world.GetContainerContents(theirCont.Uid).ToList())
            PlaceItemInPack(partner, item);

        _netState.Send(new PacketSecureTradeClose(myCont.Uid.Value));
        SendTradeCloseToPartner?.Invoke(partner, theirCont.Uid.Value);

        trade.Cancel();
        _tradeManager!.EndTrade(trade);

        myCont.Delete();
        theirCont.Delete();
    }

    private void CompleteTrade(SecureTrade trade)
    {
        var initiator = trade.Initiator;
        var partner = trade.Partner;
        var cont1 = trade.InitiatorContainer;
        var cont2 = trade.PartnerContainer;

        foreach (var item in _world.GetContainerContents(cont1.Uid).ToList())
            PlaceItemInPack(partner, item);
        foreach (var item in _world.GetContainerContents(cont2.Uid).ToList())
            PlaceItemInPack(initiator, item);

        _netState.Send(new PacketSecureTradeClose(
            trade.GetOwnContainer(_character!).Uid.Value));
        SendTradeCloseToPartner?.Invoke(
            trade.GetPartner(_character!),
            trade.GetPartnerContainer(_character!).Uid.Value);

        trade.Complete();
        _tradeManager!.EndTrade(trade);

        cont1.Delete();
        cont2.Delete();

        SysMessage("Trade complete.");
        SendTradeMessageToPartner?.Invoke(trade.GetPartner(_character!), "Trade complete.");
    }

    private void SendTradeUpdateToBoth(SecureTrade trade)
    {
        var myCont = trade.GetOwnContainer(_character!);
        bool myAcc = _character == trade.Initiator ? trade.InitiatorAccepted : trade.PartnerAccepted;
        bool theirAcc = _character == trade.Initiator ? trade.PartnerAccepted : trade.InitiatorAccepted;
        _netState.Send(new PacketSecureTradeUpdate(myCont.Uid.Value, myAcc, theirAcc));

        var partner = trade.GetPartner(_character!);
        SendTradeUpdateToPartner?.Invoke(partner, trade);
    }

    public Action<Character, Character, Item, Item>? SendTradeToPartner { get; set; }
    public Action<Character, Item, Item>? SendTradeItemToPartner { get; set; }
    public Action<Character, uint>? SendTradeCloseToPartner { get; set; }
    public Action<Character, SecureTrade>? SendTradeUpdateToPartner { get; set; }
    public Action<Character, string>? SendTradeMessageToPartner { get; set; }

    /// <summary>Handle rename request (0x75).</summary>
    public void HandleRename(uint serial, string name)
    {
        if (_character == null) return;

        // Only GM+ can rename
        if (_character.PrivLevel < PrivLevel.GM)
        {
            SysMessage(ServerMessages.Get("rename_no_permission"));
            return;
        }

        var target = _world.FindChar(new Serial(serial));
        if (target != null)
        {
            string oldName = target.Name;
            target.Name = name.Trim();
            SysMessage(ServerMessages.GetFormatted("msg_rename_success", oldName, target.Name));
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item != null)
        {
            item.Name = name.Trim();
            SysMessage(ServerMessages.GetFormatted("rename_item_ok", item.Name));
        }
    }

    /// <summary>Handle client view range change (0xC8).</summary>
    public void HandleViewRange(byte range)
    {
        // Clamp to valid range (4-24)
        if (range < 4) range = 4;
        if (range > 24) range = 24;
        _netState.ViewRange = range;
    }

    /// <summary>Open guild stone gump with member list, options.</summary>
    private void OpenGuildStoneGump(Item stone)
    {
        if (_character == null || _guildManager == null) return;

        var guild = _guildManager.GetGuild(stone.Uid);
        if (guild == null)
        {
            // No guild on this stone yet — offer to create one
            var createGump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 400, 300);
            createGump.AddResizePic(0, 0, 5054, 400, 300);
            createGump.AddText(30, 20, 0, "Guild Stone");
            createGump.AddText(30, 50, 0, "No guild is registered to this stone.");
            createGump.AddText(30, 80, 0, "Create a new guild?");
            createGump.AddButton(30, 130, 4005, 4007, 1); // Create
            createGump.AddText(70, 130, 0, "Create Guild");
            createGump.AddButton(150, 250, 4017, 4019, 0); // Cancel

            SendGump(createGump, (buttonId, switches, textEntries) =>
            {
                if (buttonId == 1)
                {
                    var newGuild = _guildManager.CreateGuild(stone.Uid, $"{_character.Name}'s Guild", _character.Uid);
                    SysMessage(ServerMessages.GetFormatted("guild_created", newGuild.Name));
                }
            });
            return;
        }

        // Show guild info gump
        var gump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 500, 520);
        gump.AddResizePic(0, 0, 5054, 500, 520);
        gump.AddText(30, 10, 0, $"Guild: {guild.Name}");
        gump.AddText(30, 30, 0, $"Abbreviation: [{guild.Abbreviation}]");
        if (!string.IsNullOrEmpty(guild.Charter))
            gump.AddText(30, 50, 0, $"Charter: {guild.Charter}");
        gump.AddText(30, 70, 0, $"Members: {guild.MemberCount} | Wars: {guild.Wars.Count()} | Allies: {guild.Allies.Count()}");

        // Member list with titles and candidate status
        int y = 100;
        int memberIdx = 0;
        foreach (var member in guild.Members)
        {
            var ch = _world.FindChar(member.CharUid);
            string memberName = ch?.Name ?? $"UID 0x{member.CharUid.Value:X}";
            string privText = member.Priv switch
            {
                GuildPriv.Master => " [Master]",
                GuildPriv.Candidate => " [Candidate]",
                _ => ""
            };
            string titleText = !string.IsNullOrEmpty(member.Title) ? $" ({member.Title})" : "";
            int hue = member.Priv == GuildPriv.Candidate ? 33 : 0; // yellow for candidates
            gump.AddText(50, y, hue, $"{memberName}{privText}{titleText}");
            y += 20;
            memberIdx++;
            if (y > 350) break;
        }

        var myMember = guild.FindMember(_character.Uid);
        int btnY = 370;

        if (myMember == null)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1); // Join
            gump.AddText(70, btnY, 0, "Request to Join");
            btnY += 25;
        }
        else if (myMember.Priv == GuildPriv.Master)
        {
            gump.AddButton(30, btnY, 4005, 4007, 2); // Disband
            gump.AddText(70, btnY, 0, "Disband Guild");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10); // Accept candidates
            gump.AddText(70, btnY, 0, "Accept Candidate");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 11); // Set title
            gump.AddText(70, btnY, 0, "Set Member Title");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12); // Declare war
            gump.AddText(70, btnY, 0, "Declare War");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 13); // Declare peace
            gump.AddText(70, btnY, 0, "Declare Peace");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14); // Set charter
            gump.AddText(70, btnY, 0, "Set Charter");
            gump.AddTextEntry(170, btnY, 250, 20, 0, 1, guild.Charter);
            btnY += 25;
        }
        else
        {
            gump.AddButton(30, btnY, 4005, 4007, 3); // Leave
            gump.AddText(70, btnY, 0, "Leave Guild");
            btnY += 25;
        }
        gump.AddButton(350, 480, 4017, 4019, 0); // Close

        var capturedGuild = guild;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleGuildGumpResponse(stone, capturedGuild, buttonId, textEntries);
        });
    }

    private void HandleGuildGumpResponse(Item stone, GuildDef guild, uint buttonId, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null || _guildManager == null) return;

        switch (buttonId)
        {
            case 1: // Join request
                guild.AddRecruit(_character.Uid);
                SysMessage(ServerMessages.Get("guild_join_request"));
                break;
            case 2: // Disband
                _guildManager.RemoveGuild(stone.Uid);
                SysMessage(ServerMessages.Get("guild_disbanded"));
                break;
            case 3: // Leave
                guild.RemoveMember(_character.Uid);
                SysMessage(ServerMessages.Get("guild_left"));
                break;
            case 10: // Accept candidate
                SysMessage(ServerMessages.Get("guild_target_candidate"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (guild.AcceptMember(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("guild_member_added", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("guild_not_candidate", target.Name));
                });
                break;
            case 11: // Set member title
                SysMessage(ServerMessages.Get("guild_target_title"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var member = guild.FindMember(target.Uid);
                    if (member == null) { SysMessage(ServerMessages.Get("guild_not_member")); return; }
                    // Use text entry if provided
                    var titleEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                    if (!string.IsNullOrWhiteSpace(titleEntry.Text))
                    {
                        member.Title = titleEntry.Text.Trim();
                        SysMessage(ServerMessages.GetFormatted("guild_title_set", target.Name, member.Title));
                    }
                    else
                        SysMessage(ServerMessages.Get("guild_no_title"));
                });
                break;
            case 12: // Declare war
                SysMessage(ServerMessages.Get("guild_target_enemy"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var enemyGuild = _guildManager.GetGuild(targetItem.Uid);
                    if (enemyGuild == null) { SysMessage(ServerMessages.Get("guild_not_stone")); return; }
                    guild.DeclareWar(targetItem.Uid);
                    SysMessage(ServerMessages.GetFormatted("guild_war_declared", enemyGuild.Name));
                });
                break;
            case 13: // Declare peace
                SysMessage(ServerMessages.Get("guild_target_peace"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    guild.DeclarePeace(targetItem.Uid);
                    SysMessage(ServerMessages.Get("guild_peace_declared"));
                });
                break;
            case 14: // Set charter
            {
                var charterEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                if (!string.IsNullOrWhiteSpace(charterEntry.Text))
                {
                    guild.Charter = charterEntry.Text.Trim();
                    SysMessage(ServerMessages.Get("guild_charter_updated"));
                }
                break;
            }
        }
    }

    /// <summary>Open house management gump from house sign or multi item.</summary>
    private void OpenHouseSignGump(Item signOrMulti)
    {
        if (_character == null || _housingEngine == null) return;

        // Find the house — could be the multi item itself or linked via tag
        var house = _housingEngine.GetHouse(signOrMulti.Uid);
        if (house == null && signOrMulti.TryGetTag("HOUSE_UID", out string? houseUidStr) &&
            uint.TryParse(houseUidStr, out uint houseUid))
        {
            house = _housingEngine.GetHouse(new Serial(houseUid));
        }

        if (house == null)
        {
            SysMessage(ServerMessages.Get("house_not_house"));
            return;
        }

        // Auto-refresh on owner visit
        _housingEngine.OnCharacterEnterHouse(_character, house);

        var priv = house.GetPriv(_character.Uid);
        var ownerCh = _world.FindChar(house.Owner);
        string ownerName = ownerCh?.Name ?? "Unknown";

        var gump = new GumpBuilder(_character.Uid.Value, signOrMulti.Uid.Value, 420, 440);
        gump.AddResizePic(0, 0, 5054, 420, 440);
        gump.AddText(30, 10, 0, "House Management");
        gump.AddText(30, 35, 0, $"Owner: {ownerName}");
        gump.AddText(30, 55, 0, $"Type: {house.Type}");
        gump.AddText(30, 75, 0, $"Storage: {house.Lockdowns.Count}/{house.MaxLockdowns} lockdowns, {house.SecureContainers.Count}/{house.MaxSecure} secure");
        gump.AddText(30, 95, 0, $"Condition: {house.DecayStage}");
        gump.AddText(30, 115, 0, $"Co-Owners: {house.CoOwners.Count}  Friends: {house.Friends.Count}");

        int btnY = 145;
        if (priv is HousePriv.Owner or HousePriv.CoOwner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1);
            gump.AddText(70, btnY, 0, "Transfer House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 2);
            gump.AddText(70, btnY, 0, "Demolish House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10);
            gump.AddText(70, btnY, 0, "Add Co-Owner");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 11);
            gump.AddText(70, btnY, 0, "Add Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12);
            gump.AddText(70, btnY, 0, "Remove Co-Owner");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 13);
            gump.AddText(70, btnY, 0, "Remove Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14);
            gump.AddText(70, btnY, 0, "Lock Down Item");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 15);
            gump.AddText(70, btnY, 0, "Release Lockdown");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 16);
            gump.AddText(70, btnY, 0, "Secure Container");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 17);
            gump.AddText(70, btnY, 0, "Release Secure");
            btnY += 25;
        }
        if (priv == HousePriv.Owner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 20);
            gump.AddText(70, btnY, 0, "Ban Player");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 21);
            gump.AddText(70, btnY, 0, "Unban Player");
            btnY += 25;
        }
        if (priv != HousePriv.None && priv != HousePriv.Ban)
        {
            gump.AddButton(30, btnY, 4005, 4007, 3);
            gump.AddText(70, btnY, 0, "Open Door");
            btnY += 25;
        }
        gump.AddButton(280, 400, 4017, 4019, 0); // Close

        var capturedHouse = house;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleHouseGumpResponse(signOrMulti, capturedHouse, buttonId);
        });
    }

    private void HandleHouseGumpResponse(Item signOrMulti, House house, uint buttonId)
    {
        if (_character == null || _housingEngine == null) return;

        switch (buttonId)
        {
            case 1: // Transfer — target the new owner
                SysMessage(ServerMessages.Get("house_select_owner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer)
                    {
                        SysMessage(ServerMessages.Get("msg_invalid_target"));
                        return;
                    }
                    house.TransferOwnership(target.Uid);
                    SysMessage(ServerMessages.GetFormatted("house_transferred", target.Name));
                });
                break;
            case 2: // Demolish
                var deed = _housingEngine.RemoveHouse(signOrMulti.Uid, _character);
                if (deed != null)
                    SysMessage(ServerMessages.Get("house_demolished"));
                else
                    SysMessage(ServerMessages.Get("house_cant_demolish"));
                break;
            case 3: // Open door
                SysMessage(ServerMessages.Get("house_door_opened"));
                break;
            case 10: // Add Co-Owner
                SysMessage(ServerMessages.Get("house_add_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_coowner", target.Name));
                });
                break;
            case 11: // Add Friend
                SysMessage(ServerMessages.Get("house_add_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_friend", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_friend", target.Name));
                });
                break;
            case 12: // Remove Co-Owner
                SysMessage(ServerMessages.Get("house_remove_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_coowner"));
                });
                break;
            case 13: // Remove Friend
                SysMessage(ServerMessages.Get("house_remove_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_friend", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_friend"));
                });
                break;
            case 14: // Lock Down Item
                SysMessage(ServerMessages.Get("house_lockdown"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.Lockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_fail"));
                });
                break;
            case 15: // Release Lockdown
                SysMessage(ServerMessages.Get("house_lockdown_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.ReleaseLockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_released"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_not"));
                });
                break;
            case 16: // Secure Container
                SysMessage(ServerMessages.Get("house_secure"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.SecureContainer(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_fail"));
                });
                break;
            case 17: // Release Secure
                SysMessage(ServerMessages.Get("house_secure_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.ReleaseSecure(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_released"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_not"));
                });
                break;
            case 20: // Ban
                SysMessage(ServerMessages.Get("house_ban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddBan(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_banned", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_banned", target.Name));
                });
                break;
            case 21: // Unban
                SysMessage(ServerMessages.Get("house_unban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveBan(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_unbanned", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_banned"));
                });
                break;
        }
    }


    public void OpenDoor()
    {
        if (_character == null) return;
        foreach (var item in _world.GetItemsInRange(_character.Position, 2))
        {
            if (item.ItemType == ItemType.Door || item.ItemType == ItemType.DoorLocked ||
                item.ItemType == ItemType.Portculis || item.ItemType == ItemType.PortLocked)
            {
                ToggleDoor(item);
                return;
            }
        }
    }

    private void ToggleDoor(Item door)
    {
        if (_character == null) return;

        // Door art IDs toggle between open/closed variants (±1 or ±2 offset)
        bool isOpen = door.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1";

        if (isOpen)
        {
            door.BaseId = (ushort)(door.BaseId - 1);
            door.RemoveTag("DOOR_OPEN");
        }
        else
        {
            door.BaseId = (ushort)(door.BaseId + 1);
            door.SetTag("DOOR_OPEN", "1");
        }

        // Play door sound and broadcast updated item to nearby clients
        ushort soundId = (ushort)(isOpen ? 0x00F1 : 0x00EA); // close/open sounds
        var soundPacket = new PacketSound(soundId, door.X, door.Y, door.Z);
        BroadcastNearby?.Invoke(door.Position, UpdateRange, soundPacket, 0);

        var itemPacket = new PacketWorldItem(
            door.Uid.Value, door.DispIdFull, door.Amount,
            door.X, door.Y, door.Z, door.Hue);
        BroadcastNearby?.Invoke(door.Position, UpdateRange, itemPacket, 0);
    }

    private void UsePotion(Item potion)
    {
        if (_character == null) return;

        // Determine potion effect from BaseId ranges
        // Common UO potion base IDs: 0x0F06-0x0F0D heal, 0x0F07 cure, 0x0F0B refresh etc.
        string potionType = "heal"; // default
        if (potion.TryGetTag("POTION_TYPE", out string? pType) && pType != null)
            potionType = pType.ToLowerInvariant();

        switch (potionType)
        {
            case "heal":
            case "greatheal":
                int healAmount = potionType == "greatheal" ? 20 : 10;
                _character.Hits = (short)Math.Min(_character.Hits + healAmount, _character.MaxHits);
                SysMessage(ServerMessages.GetFormatted("potion_heal", healAmount));
                break;
            case "cure":
                _character.ClearStatFlag(StatFlag.Poisoned);
                SysMessage(ServerMessages.Get("potion_cured"));
                break;
            case "refresh":
            case "totalrefresh":
                int stamAmount = potionType == "totalrefresh" ? 60 : 25;
                _character.Stam = (short)Math.Min(_character.Stam + stamAmount, _character.MaxStam);
                SysMessage(ServerMessages.GetFormatted("potion_stamina", stamAmount));
                break;
            case "strength":
                _character.Str += 10;
                SysMessage(ServerMessages.Get("potion_str"));
                break;
            case "agility":
                _character.Dex += 10;
                SysMessage(ServerMessages.Get("potion_dex"));
                break;
            default:
                SysMessage(ServerMessages.Get("potion_drink"));
                break;
        }

        // Play drink sound
        var soundPacket = new PacketSound(0x0031, _character.X, _character.Y, _character.Z);
        _netState.Send(soundPacket);

        // Update stats
        SendCharacterStatus(_character);

        // Consume potion. Source-X parity: @Destroy RETURN 1 keeps the bottle.
        if (_triggerDispatcher?.FireItemTrigger(potion, ItemTrigger.Destroy,
                new TriggerArgs { CharSrc = _character, ItemSrc = potion }) != TriggerResult.True)
        {
            potion.Delete();
        }
    }

    /// <summary>Handle UseSkill request (from packet 0x12 or extended command).</summary>
    public void HandleUseSkill(int skillId)
    {
        if (_character == null || _character.IsDead) return;
        if (skillId < 0 || skillId >= (int)SkillType.Qty) return;

        var skill = (SkillType)skillId;

        // Source-X parity: information skills prompt for a target before emitting
        // any message. Route through the info-skill pipeline in BeginInfoSkill so
        // the player sees the exact CClientTarg.cpp text sequence.
        if (SkillHandlers.IsInfoSkill(skill))
        {
            BeginInfoSkill(skill, skillId);
            return;
        }

        // Active skills with parity coverage in ActiveSkillEngine.
        var activeKind = SkillHandlers.GetActiveSkillTarget(skill);
        if (activeKind != SkillHandlers.ActiveSkillTargetKind.Unsupported)
        {
            BeginActiveSkill(skill, skillId, activeKind);
            return;
        }

        // Fire @SkillPreStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var preResult = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (preResult == TriggerResult.True)
                return;
        }

        // Fire @SkillStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (result == TriggerResult.True)
                return;
        }

        // Fire @SkillStroke — the main action moment
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
            new TriggerArgs { CharSrc = _character, N1 = skillId });

        bool success = _skillHandlers?.UseSkill(_character, skill) ?? false;

        // Fire @SkillSuccess or @SkillFail
        if (_triggerDispatcher != null)
        {
            var trigger = success ? CharTrigger.SkillSuccess : CharTrigger.SkillFail;
            _triggerDispatcher.FireCharTrigger(_character, trigger,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
        }

        if (success)
            SysMessage(ServerMessages.GetFormatted("skill_use_ok", skill));
        else
            SysMessage(ServerMessages.GetFormatted("skill_use_fail", skill));
    }

    /// <summary>Handle extended command (0xBF sub-commands).</summary>
    public void HandleExtendedCommand(ushort subCmd, byte[] data)
    {
        switch (subCmd)
        {
            case 0x001A: // stat lock change
                if (data.Length >= 2 && _character != null)
                {
                    byte stat = data[0];
                    byte lockVal = data[1];
                    // stat: 0=str, 1=dex, 2=int — store as tags
                    _character.SetTag($"STATLOCK.{stat}", lockVal.ToString());
                }
                break;
            case 0x0013: // context menu request
                if (data.Length >= 4)
                {
                    uint targetSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                    SendContextMenu(targetSerial);
                }
                break;
            case 0x0015: // context menu response
                if (data.Length >= 6)
                {
                    uint respSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                    ushort entryTag = (ushort)((data[4] << 8) | data[5]);
                    HandleContextMenuResponse(respSerial, entryTag);
                }
                break;
            case 0x0006: // party commands
                if (data.Length >= 1)
                    HandlePartyCommand(data);
                break;
            case 0x0024: // unknown / unused in most clients
                break;
            case 0x000B: // Chat button on paperdoll — client requests chat window
                if (_character != null)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserChatButton,
                        new TriggerArgs { CharSrc = _character });
                }
                break;
            case 0x0028: // Guild button on paperdoll
                if (_character != null)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserGuildButton,
                        new TriggerArgs { CharSrc = _character });
                }
                break;
            case 0x0032: // Quest button on paperdoll
                if (_character != null)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserQuestButton,
                        new TriggerArgs { CharSrc = _character });
                }
                break;
            case 0x002C: // Invoke virtue — client passes virtue id in data[0]
                if (_character != null && data.Length >= 1)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserVirtueInvoke,
                        new TriggerArgs { CharSrc = _character, N1 = data[0] });
                }
                break;
        }
    }

    /// <summary>
    /// Handle party sub-commands (0xBF sub 0x0006).
    /// Sub-types: 1=Add, 2=Remove, 3=PrivateMsg, 4=PublicMsg, 6=SetLoot, 8=Accept, 9=Decline.
    /// </summary>
    private void HandlePartyCommand(byte[] data)
    {
        if (_character == null || _partyManager == null) return;
        byte partyCmd = data[0];

        switch (partyCmd)
        {
            case 1: // Add member (invite)
                if (data.Length >= 5)
                {
                    uint targetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    var target = _world.FindChar(new Serial(targetUid));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }

                    var existingParty = _partyManager.FindParty(_character.Uid);
                    if (existingParty != null && existingParty.IsFull) { SysMessage(ServerMessages.Get("party_is_full")); return; }

                    // Fire @PartyInvite trigger on target
                    _triggerDispatcher?.FireCharTrigger(target, CharTrigger.PartyInvite,
                        new TriggerArgs { CharSrc = _character });

                    // Store pending invite and send invite packet to target
                    target.SetTag("PARTY_INVITE_FROM", _character.Uid.Value.ToString());
                    SendToChar?.Invoke(target.Uid, new PacketPartyInvitation(_character.Uid.Value));
                    SysMessage(ServerMessages.GetFormatted("party_invite", target.Name));
                }
                break;

            case 2: // Remove member
                if (data.Length >= 5)
                {
                    uint removeUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party == null) break;
                    if (party.Master != _character.Uid && new Serial(removeUid) != _character.Uid)
                    {
                        SysMessage(ServerMessages.Get("party_notleader"));
                        break;
                    }
                    // Fire @PartyRemove trigger on removed member
                    var removedChar = _world.FindChar(new Serial(removeUid));
                    if (removedChar != null)
                        _triggerDispatcher?.FireCharTrigger(removedChar, CharTrigger.PartyRemove,
                            new TriggerArgs { CharSrc = _character });

                    _partyManager.Leave(new Serial(removeUid));
                    SysMessage(ServerMessages.Get("party_leave_1"));
                    BroadcastPartyUpdate(party, new Serial(removeUid));
                }
                break;

            case 3: // Private party message
                if (data.Length >= 5)
                {
                    uint pmTargetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    string pmMsg = data.Length > 5
                        ? System.Text.Encoding.BigEndianUnicode.GetString(data, 5, data.Length - 5).TrimEnd('\0')
                        : "";
                    if (!string.IsNullOrEmpty(pmMsg))
                    {
                        SendToChar?.Invoke(new Serial(pmTargetUid),
                            new PacketPartyMessage(_character.Uid.Value, pmMsg, isPrivate: true));
                        SysMessage(ServerMessages.GetFormatted("party_msg", $"{pmTargetUid:X}", pmMsg));
                    }
                }
                break;

            case 4: // Public party message
                if (data.Length >= 2)
                {
                    string msg = System.Text.Encoding.BigEndianUnicode.GetString(data, 1, data.Length - 1).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(msg)) break;
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party != null)
                    {
                        var chatPacket = new PacketPartyMessage(_character.Uid.Value, msg);
                        foreach (var memberUid in party.Members)
                            SendToChar?.Invoke(memberUid, chatPacket);
                    }
                }
                break;

            case 6: // Set loot flag
                if (data.Length >= 2)
                {
                    bool canLoot = data[1] != 0;
                    var party = _partyManager.FindParty(_character.Uid);
                    party?.SetLootFlag(_character.Uid, canLoot);
                    SysMessage(canLoot ? "Party loot sharing enabled." : "Party loot sharing disabled.");
                }
                break;

            case 8: // Accept invite
            {
                if (_character.TryGetTag("PARTY_INVITE_FROM", out string? inviterStr) &&
                    uint.TryParse(inviterStr, out uint inviterUid))
                {
                    _partyManager.AcceptInvite(new Serial(inviterUid), _character.Uid);
                    _character.RemoveTag("PARTY_INVITE_FROM");
                    SysMessage(ServerMessages.Get("party_added"));
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party != null) BroadcastPartyUpdate(party);
                }
                break;
            }

            case 9: // Decline invite
            {
                if (_character.TryGetTag("PARTY_INVITE_FROM", out string? declineInviterStr) &&
                    uint.TryParse(declineInviterStr, out uint declineInviterUid))
                {
                    SendToChar?.Invoke(new Serial(declineInviterUid), null!); // notify inviter
                }
                _character.RemoveTag("PARTY_INVITE_FROM");
                SysMessage(ServerMessages.Get("party_decline_2"));
                break;
            }
        }
    }

    /// <summary>Send party member list update to all members.</summary>
    private void BroadcastPartyUpdate(PartyDef party, Serial? removedMember = null)
    {
        var memberSerials = party.Members.Select(m => m.Value).ToArray();
        if (removedMember.HasValue)
        {
            var removePacket = new PacketPartyRemoveMember(removedMember.Value.Value, memberSerials);
            foreach (var memberUid in party.Members)
                SendToChar?.Invoke(memberUid, removePacket);
            SendToChar?.Invoke(removedMember.Value, removePacket);
        }
        else
        {
            var listPacket = new PacketPartyMemberList(memberSerials);
            foreach (var memberUid in party.Members)
                SendToChar?.Invoke(memberUid, listPacket);
        }
    }

    private void SendContextMenu(uint targetSerial)
    {
        if (_character == null) return;

        var entries = new List<(ushort EntryTag, uint ClilocId, ushort Flags)>();

        var ch = _world.FindChar(new Serial(targetSerial));
        if (ch != null)
        {
            entries.Add((1, 3006123, 0)); // Open Paperdoll
            if (ch == _character)
            {
                entries.Add((2, 3006145, 0)); // Open Backpack
            }
            if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Vendor)
            {
                entries.Add((3, 3006103, 0)); // Buy
                entries.Add((4, 3006106, 0)); // Sell
            }
            if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Banker)
            {
                entries.Add((5, 3006105, 0)); // Open Bankbox
            }
            // Mount / Dismount: exposed as a context-menu action so the client
            // does not require a DoubleClick to saddle. Double-click remains
            // equivalent. Entry is filtered by IsMountable so non-ridable
            // mobs (monsters, humans) don't get a useless "Mount Me" line.
            if (!ch.IsPlayer && ch != _character &&
                Mounts.MountEngine.IsMountable(ch.BodyId))
            {
                entries.Add((6, 3006155, 0)); // Mount Me
            }
            if (ch == _character && _character.IsMounted)
            {
                entries.Add((7, 3006112, 0)); // Dismount
            }
        }

        if (entries.Count > 0)
            _netState.Send(new PacketContextMenu(targetSerial, entries.ToArray()));
    }

    private void HandleContextMenuResponse(uint targetSerial, ushort entryTag)
    {
        if (_character == null) return;

        switch (entryTag)
        {
            case 1: // Open Paperdoll
                var ch = _world.FindChar(new Serial(targetSerial));
                if (ch != null) SendPaperdoll(ch);
                break;
            case 2: // Open Backpack
                if (_character.Backpack != null)
                    SendOpenContainer(_character.Backpack);
                break;
            case 3: // Buy
                var vendor = _world.FindChar(new Serial(targetSerial));
                if (vendor != null) HandleVendorInteraction(vendor);
                break;
            case 4: // Sell
                SysMessage(ServerMessages.Get("vendor_what_sell"));
                break;
            case 5: // Open Bankbox
                SysMessage(ServerMessages.Get("vendor_bank_unavailable"));
                break;
            case 6: // Mount Me
                HandleDoubleClick(targetSerial);
                break;
            case 7: // Dismount
                _mountEngine?.Dismount(_character);
                break;
        }
    }

    // ==================== Single Click ====================
}
