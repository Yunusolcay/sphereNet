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

/// <summary>
/// Inventory/interaction handler extracted from the GameClient.Inventory
/// partial (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Single click, item pickup/drop/equip, drag bookkeeping, profile and
/// status requests. The method bodies moved verbatim; the private context
/// shims below enumerate exactly what this handler needs from GameClient.
/// </summary>
public sealed class ClientInventoryHandler
{
    private readonly IClientContext _client;

    internal ClientInventoryHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private HousingEngine? _housingEngine => _client.Housing;
    private const int UpdateRange = GameClient.UpdateRange;
    private static NotorietyHueSettings NotorietyHues => GameClient.NotorietyHues;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private Action<Point3D, int, uint, Action<Character, GameClient>>? ForEachClientInRange => _client.ForEachClientInRange;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private byte GetNotoriety(Character ch) => _client.GetNotoriety(ch);
    private void SendPickupFailed(byte reason) => _client.SendPickupFailed(reason);
    private TradeManager? _tradeManager => _client.TradeM;
    private void PlaceItemInPack(Character target, Item item) => _client.PlaceItemInPack(target, item);

    /// <summary>Source-X NPC_OnTrainPay: consume gold handed to the trainer as
    /// skill points (1 gp = 0.1) up to the offered cap. Returns false when the
    /// pending offer doesn't match this NPC (normal gift handling continues).
    /// Leftover gold above the cap bounces back to the student's pack.</summary>
    /// <summary>Source-X NPC_OnHirePay/NPC_OnHirePayMore: gold given to an NPC
    /// whose chardef sets HIREDAYWAGE (or a legacy HIRE_WAGE tag) funds the
    /// NPC's own prepaid wage balance. First payment hires (ownership +
    /// follow); later payments extend the balance. Refuses another player's
    /// hireling and payments below one day's wage.</summary>
    private bool TryApplyHirePayment(Character npc, Item gold)
    {
        if (_character == null) return false;
        uint dayWage = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.HireDayWage ?? 0;
        if (dayWage == 0 && npc.TryGetTag("HIRE_WAGE", out string? w) &&
            uint.TryParse(w, out uint tagWage))
            dayWage = tagWage;
        if (dayWage == 0)
            return false;

        var owner = npc.ResolveOwnerCharacter();
        if (owner != null && owner != _character)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetNotForHire));
            return true; // consumed the interaction, bounce handled below
        }

        if (owner == null && gold.Amount < dayWage)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetNotEnough));
            return false; // gift path may still bounce it back
        }

        if (owner == null &&
            !npc.TryAssignOwnership(_character, _character, summoned: false, enforceFollowerCap: true))
            return false;

        long balance = npc.TryGetTag("HIRE_BALANCE", out string? bs) &&
            long.TryParse(bs, out long b) ? b : 0;
        balance += gold.Amount;
        npc.SetTag("HIRE_BALANCE", balance.ToString());
        if (owner == null)
        {
            npc.PetAIMode = PetAIMode.Follow;
            npc.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
        }
        long daysPaid = balance / Math.Max(1, dayWage);
        SysMessage(ServerMessages.GetFormatted(Msg.NpcPetHireTime, daysPaid.ToString()));
        gold.RemoveFromWorld(); // consumed into the wage balance
        return true;
    }

    private bool TryApplyTrainPayment(Character trainer, Item gold, string pending)
    {
        if (_character == null) return false;
        var parts = pending.Split('|');
        if (parts.Length < 3 ||
            !uint.TryParse(parts[0], out uint npcUid) || npcUid != trainer.Uid.Value ||
            !int.TryParse(parts[1], out int skillId) ||
            !int.TryParse(parts[2], out int maxTrain) ||
            skillId < 0 || skillId >= SkillEngine.BaseSkillCount)
            return false;

        var skill = (SkillType)skillId;
        int current = _character.GetSkill(skill);
        if (current >= maxTrain)
        {
            _character.RemoveTag("TRAIN_PENDING");
            PlaceItemInPack(_character, gold);
            SysMessage("You already know all this trainer can teach.");
            return true;
        }

        int points = Math.Min(gold.Amount, maxTrain - current);
        _character.SetSkill(skill, (ushort)(current + points));
        SysMessage($"Your {skill} rises to {(current + points) / 10.0:0.#}.");

        if (current + points >= maxTrain)
            _character.RemoveTag("TRAIN_PENDING");

        // The trainer keeps the fee; any overpayment returns to the student.
        if (points >= gold.Amount)
        {
            gold.RemoveFromWorld();
        }
        else
        {
            gold.Amount -= (ushort)points;
            PlaceItemInPack(_character, gold);
        }
        return true;
    }
    private void SendTradeUpdateToBoth(SecureTrade trade) => _client.SendTradeUpdateToBoth(trade);
    private Action<Character, Item, Item>? SendTradeItemToPartner => _client.SendTradeItemToPartner;
    private SpellEngine? _spellEngine => _client.Spells;
    private bool CanSendStatusFor(Character ch) => _client.CanSendStatusFor(ch);
    private void SendSkillList() => _client.SendSkillList();
    private bool InitiateTrade(Character partner, Item? firstItem = null) => _client.InitiateTrade(partner, firstItem);
    private void SendCharacterStatus(Character ch, bool includeExtendedStats = true) => _client.SendCharacterStatus(ch, includeExtendedStats);
    private void BroadcastWorldItem(Item item)
    {
        if (ForEachClientInRange != null)
        {
            ForEachClientInRange(item.Position, UpdateRange, 0, (_, observer) =>
            {
                observer.SendWorldItem(item);
                observer.View.KnownItems.Add(item.Uid.Value);
                observer.View.LastKnownItemState[item.Uid.Value] =
                    (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
            });
            return;
        }

        BroadcastNearby?.Invoke(item.Position, UpdateRange,
            _client.BuildWorldItemPacket(item.Uid.Value, item.DispIdFull, item.Amount,
                item.X, item.Y, item.Z, item.Hue, item.Direction), 0);
    }

    public void HandleSingleClick(uint uid)
    {
        if (_character == null) return;

        var obj = _world.FindObject(new Serial(uid));
        if (obj == null) return;

        if (obj is Character clickTarget &&
            clickTarget.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible) &&
            _character.PrivLevel < PrivLevel.Counsel)
            return;

        // Fire @Click trigger (and the legacy @ToolTip — old-style single-click
        // name/tooltip request. IsTrigUsed-gated: single click is a hot path).
        if (_triggerDispatcher != null)
        {
            if (obj is Character clickCh)
            {
                if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.ToolTip) &&
                    _triggerDispatcher.FireCharTrigger(clickCh, CharTrigger.ToolTip,
                        new TriggerArgs { CharSrc = _character, ScriptConsole = _client }) == TriggerResult.True)
                    return;
                var result = _triggerDispatcher.FireCharTrigger(clickCh, CharTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ScriptConsole = _client });
                if (result == TriggerResult.True)
                    return;
            }
            else if (obj is Item clickItem)
            {
                if (_triggerDispatcher.IsItemTriggerUsed(ItemTrigger.Tooltip) &&
                    _triggerDispatcher.FireItemTrigger(clickItem, ItemTrigger.Tooltip,
                        new TriggerArgs { CharSrc = _character, ItemSrc = clickItem, ScriptConsole = _client }) == TriggerResult.True)
                    return;
                var result = _triggerDispatcher.FireItemTrigger(clickItem, ItemTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ItemSrc = clickItem, ScriptConsole = _client });
                if (result == TriggerResult.True)
                    return;
            }
        }

        // Overhead name: for characters, the hue follows notoriety so the
        // label reads blue/green/grey/orange/red/yellow. Items stay grey.
        ushort nameHue = 0x03B2;
        if (obj is Character labelCh)
            nameHue = NotoToHue(GetNotoriety(labelCh), labelCh);

        // Containers and corpses append their content summary on single
        // click — "corpse of X (3 items, 25 stones)" (CONT_ITEMS defmessage).
        // Pile items prefix the stack amount ("1234 gold coins") via GetDisplayName.
        string label = obj is Item nameItem ? nameItem.GetDisplayName() : obj.GetName();
        if (obj is Item contItem &&
            contItem.ItemType is ItemType.Container or ItemType.Corpse &&
            contItem.Contents.Count > 0)
        {
            int tenths = 0;
            foreach (var inner in contItem.Contents)
                tenths += inner.TotalWeightTenths;
            int stones = tenths / Item.WeightUnits;
            label += ServerMessages.GetFormatted(Msg.ContItems, contItem.Contents.Count, stones);
        }
        else if (obj is Character guildedCh)
        {
            // A guilded player's overhead name carries the guild abbreviation
            // (e.g. "Lord Yunus [ABC]") when the member keeps it visible.
            label += GuildAbbrevSuffix(guildedCh);
        }

        _netState.Send(new PacketSpeechUnicodeOut(
            uid, (ushort)(obj is Character c ? c.BodyId : 0),
            6, nameHue, 3, "TRK", "", label));

        if (_triggerDispatcher != null)
        {
            if (obj is Character afterClickCh)
            {
                _triggerDispatcher.FireCharTrigger(afterClickCh, CharTrigger.AfterClick,
                    new TriggerArgs { CharSrc = _character, ScriptConsole = _client });
            }
            else if (obj is Item afterClickItem)
            {
                _triggerDispatcher.FireItemTrigger(afterClickItem, ItemTrigger.AfterClick,
                    new TriggerArgs { CharSrc = _character, ItemSrc = afterClickItem, ScriptConsole = _client });
            }
        }
    }

    /// <summary>The " [ABBR]" suffix appended to a guilded player's overhead name,
    /// or empty when they are unguilded or have hidden their abbreviation.</summary>
    private string GuildAbbrevSuffix(Character ch) =>
        _client.GuildM?.GetAbbrevSuffix(ch.Uid) ?? "";

    private bool IsInsideContainer(Item container, Serial parentUid, int maxDepth = 16)
    {
        var current = container;
        for (int i = 0; i < maxDepth && current != null; i++)
        {
            if (current.Uid == parentUid) return true;
            if (!current.ContainedIn.IsValid) break;
            current = _world.FindItem(current.ContainedIn);
        }
        return false;
    }

    internal Item? GetTopContainer(Item item)
    {
        var current = item;
        for (int i = 0; i < 16 && current.ContainedIn.IsValid; i++)
        {
            var parent = _world.FindItem(current.ContainedIn);
            if (parent == null) break;
            current = parent;
        }
        return current;
    }

    /// <summary>True when a live banker NPC is within reach (Source-X bank-open
    /// proximity). The bank box is only manipulable while near a banker — the box was
    /// opened at one — so pickup/drop into the self bank box re-checks this.</summary>
    private bool IsNearBanker(Character ch)
    {
        foreach (var other in _world.GetCharsInRange(ch.Position, 3))
        {
            if (!other.IsDead && other.NpcBrain == NpcBrainType.Banker &&
                other.MapIndex == ch.MapIndex)
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="item"/>'s top-level container is THIS
    /// character's own bank box (directly or via a nested bag).</summary>
    /// <summary>Source-X CChar::NPC_OnItemGive (CCharNPCAct.cpp:2060) — the
    /// native give flow after @ReceiveItem and the train/hire gold pay:
    /// an owned pet eats offered food and refuses what it cannot carry; a
    /// banker deposits gold into the giver's bank box; dropping goods on a
    /// vendor is a quick-sell offer; everyone else runs the
    /// NPC_WantThisItem gate — an UNWANTED item is refused by default,
    /// @NPCRefuseItem RETURN 1 overrides the refusal (opens the accept
    /// path), and @NPCAcceptItem RETURN 1 cancels the native accept.</summary>
    private void HandleGiveItemToNpc(Character npc, Item item)
    {
        bool isGold = item.ItemType == ItemType.Gold || item.BaseId == 0x0EED;

        // Own pet / hireling.
        if (npc.HasOwner(_character!.Uid))
        {
            // Owned VENDOR: goods go into the vendor stock box (Source-X
            // GetBank(LAYER_VENDOR_STOCK)); gold reached the hire-pay path
            // earlier in the drop flow.
            if (npc.NpcBrain == NpcBrainType.Vendor && !isGold)
            {
                var stock = npc.GetEquippedItem(Layer.VendorStock);
                if (stock == null)
                {
                    stock = _world.CreateItem();
                    stock.BaseId = 0x0E75;
                    stock.ItemType = ItemType.Container;
                    stock.Name = "Vendor Stock";
                    npc.Equip(stock, Layer.VendorStock);
                }
                if (stock.TryAddItem(item))
                {
                    SysMessage("Your vendor adds the item to its stock.");
                    _netState.Send(new PacketDropAck());
                    return;
                }
            }

            // The pet eats an offered meal on the spot — Food_CanEat honors
            // the chardef FOODTYPE diet (a carnivore refuses an apple).
            bool edible = Character.NpcCanEatFood?.Invoke(npc, item)
                ?? item.ItemType is ItemType.Food or ItemType.Fruit
                    or ItemType.Grain or ItemType.FoodRaw;
            if (edible)
            {
                // The pet eats what it has room for and no more. Source-X hands the
                // offer to Use_EatQty, which refuses outright when the creature is
                // full (CCharUse.cpp:891) and otherwise takes only the units the free
                // space calls for (:894), then NPC_OnItemGive returns the rest to the
                // player (CCharNPCAct.cpp:2119). SphereNet added ten per unit and
                // destroyed the whole stack either way, so a hundred rations dropped
                // on a full pet vanished. It also fired no @Eat, so nothing a shard
                // scripted around feeding ever ran.
                int eaten = SphereNet.Game.NPCs.EatEngine.Eat(
                    npc, item, _triggerDispatcher, item.Amount);
                if (eaten <= 0)
                {
                    SysMessage("Your pet is not hungry.");
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropAck());
                    return;
                }

                SysMessage("Your pet gratefully eats the food.");
                if (eaten >= item.Amount)
                {
                    _world.RemoveItem(item);
                    item.Delete();
                }
                else
                {
                    item.Amount = (ushort)(item.Amount - eaten);
                    PlaceItemInPack(_character, item);
                }
                _netState.Send(new PacketDropAck());
                return;
            }
            if (!npc.CanCarry(item))
            {
                SysMessage("Your pet is too weak to carry that.");
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }
            PlaceItemInPack(npc, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        // Banker: gold goes into the GIVER's bank box.
        if (isGold && npc.NpcBrain == NpcBrainType.Banker)
        {
            var bank = _character.GetEquippedItem(Layer.BankBox);
            if (bank == null)
            {
                bank = _world.CreateItem();
                bank.BaseId = 0x09AB; // bank box container graphic
                bank.ItemType = ItemType.EqBankBox;
                bank.Name = "Bank Box";
                _character.Equip(bank, Layer.BankBox);
            }
            SysMessage($"{item.Amount} gold has been deposited into your bank box.");
            if (!bank.TryAddItem(item))
                PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        // Source-X: gold handed to any NON-banker NPC (after the train/hire
        // and pet paths above) is refused outright — a vendor never pockets
        // it, even though it "wants" gold for the ground-pickup score.
        if (isGold)
        {
            SysMessage("It does not seem to want your gold.");
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        // Non-pet vendor: a dropped item is a quick-sell offer
        // (Source-X routes it into Event_VendorSell for that one item).
        if (!isGold && npc.NpcBrain == NpcBrainType.Vendor && !npc.OwnerSerial.IsValid)
        {
            // Back into the pack first — the sell transaction consumes it
            // from there like a normal 0x9F response.
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            (_client as GameClient)?.HandleVendorSell(npc.Uid.Value,
                [new SphereNet.Network.Packets.Incoming.VendorSellEntry
                    { ItemSerial = item.Uid.Value, Amount = item.Amount }]);
            return;
        }

        // NPC_WantThisItem gate: an unwanted gift is refused by default;
        // @NPCRefuseItem RETURN 1 overrides the refusal.
        int want = Character.NpcWantThisItem?.Invoke(npc, item) ?? 0;
        if (want <= 0)
        {
            var refuse = _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCRefuseItem,
                new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item });
            if (refuse != TriggerResult.True)
            {
                SysMessage("It does not seem to want that.");
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }
        }

        // @NPCAcceptItem RETURN 1 cancels the native accept.
        if (_triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCAcceptItem,
                new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item })
            == TriggerResult.True)
        {
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        PlaceItemInPack(npc, item);
        _netState.Send(new PacketDropAck());
    }

    private bool IsInSelfBankBox(Character ch, Item item)
    {
        var top = GetTopContainer(item);
        return top != null && top.EquipLayer == Layer.BankBox && top.ContainedIn == ch.Uid;
    }

    /// <summary>Walk up the containment chain to the nearest enclosing corpse, or
    /// null if the item is not (transitively) inside a corpse. Used by the looting-
    /// crime check so taking an item from a sub-pack inside a corpse still counts.</summary>
    private Item? FindEnclosingCorpse(Item item)
    {
        var current = item;
        for (int i = 0; i < 16 && current.ContainedIn.IsValid; i++)
        {
            var parent = _world.FindItem(current.ContainedIn);
            if (parent == null) break;
            if (parent.ItemType == ItemType.Corpse) return parent;
            current = parent;
        }
        return null;
    }

    /// <summary>Convert a notoriety byte (1-7) to the hue used for
    /// overhead labels and system speech. Values mirror Source-X
    /// CServerConfig::m_iColorNoto* defaults:
    /// good/innocent=0x59 blue, guild-same=0x3f green, neutral=0x3b2 grey,
    /// criminal=0x3b2 grey, guild-war=0x90 orange, evil/murderer=0x22 red,
    /// invul=0x35 yellow. Values are configurable through sphere.ini ColorNoto* keys.</summary>
    private static ushort NotoToHue(byte noto, Character subject)
    {
        var hues = NotorietyHues;
        return noto switch
        {
            1 => !subject.IsPlayer && subject.NpcBrain != NpcBrainType.None ? hues.GoodNpc : hues.Good,
            2 => hues.GuildSame,
            4 => hues.Criminal,
            5 => hues.GuildWar,
            6 => hues.Evil,
            7 => subject.PrivLevel >= PrivLevel.GM ? hues.InvulGameMaster : hues.Invul,
            3 => hues.Neutral,
            _ => hues.Default,
        };
    }

    // ==================== Item Pick Up ====================

    // Picks the pickup-source trigger matching where the item is being taken from.
    // Equipment and stack-splits are distinguished from the plain pack/ground cases
    // so scripts can gate each independently (Source-X PICKUP_SELF / PICKUP_STACK).
    // Equipped items report ContainedIn = the wearer, so the equip check must come
    // before the container check to avoid misclassifying worn items as pack pickups.
    private static ItemTrigger SelectPickupTrigger(Item item, ushort amount)
    {
        if (item.IsEquipped) return ItemTrigger.PickupSelf;
        if (amount > 0 && amount < item.Amount && item.Amount > 1) return ItemTrigger.PickupStack;
        if (item.ContainedIn.IsValid) return ItemTrigger.PickupPack;
        return ItemTrigger.PickupGround;
    }

    public void HandleItemPickup(uint serial, ushort amount)
    {
        if (_character == null) return;
        if (_character.IsDead)
        {
            SendPickupFailed(1);
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item == null)
        {
            SendPickupFailed(5); // doesn't exist
            return;
        }

        if (_housingEngine != null && !_housingEngine.CanPickupHouseItem(_character, item))
        {
            SendPickupFailed(1);
            return;
        }

        // Central flag gate (Source-X CChar::CanMoveItem): ATTR_MOVE_NEVER items
        // (corpses, static furniture) never drag, a frozen mover can't lift, and
        // an equipped cursed item refuses to leave its layer.
        // Dead is already rejected above; housing/distance/looting stay inline.
        if (!ItemMoveRules.CanMove(_character, item, out var moveDenial))
        {
            if (moveDenial == ItemMoveRules.MoveDenial.ItemCursed)
                SysMessage(ServerMessages.Get(Msg.CantmoveCursed));
            SendPickupFailed(1);
            return;
        }

        // Snoop guard: an item may only leave a container this client has actually
        // been shown. Source-X ItemPickup rejects a pickup whose container is not in
        // m_openedContainers (CCharAct.cpp:2895). Without it, knowing a child's uid
        // was enough to reach into a locked chest that had never been opened.
        if (!CanReachInsideContainer(item))
        {
            SendPickupFailed(1);
            return;
        }

        // A client may only hold ONE item on the cursor. Source-X resolves the
        // conflict in CanEquipLayer, which bounces whatever is on LAYER_DRAGGING
        // before the new item takes the layer (CCharStatus.cpp:459). Overwriting the
        // DRAGGING tag and the lift origin instead left the previous item parented
        // to the character with no layer and no drag - present in the world but
        // absent from the pack, the equipment and the cursor alike.
        if (!ResolvePreviousDrag(item))
            return;

        // Stamp the lift origin BEFORE any reparent so a failed drop can bounce the
        // item back where it came from (Source-X). Overwritten on every pickup.
        CaptureDragOrigin(item);

        var (dragSourceSerial, dragSourcePos) = GetDragSource(item);

        // Fire the pickup trigger, choosing the most specific source variant:
        // Self  = dragged off the character's own equipment layers,
        // Stack = a partial amount split out of a larger stack,
        // Pack  = taken from inside a container, Ground = loose on the ground.
        if (_triggerDispatcher != null)
        {
            var trigger = SelectPickupTrigger(item, amount);
            var result = _triggerDispatcher.FireItemTrigger(item, trigger,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            if (result == TriggerResult.True)
            {
                SendPickupFailed(1);
                return;
            }
        }

        if (_character.PrivLevel < PrivLevel.GM)
        {
            if (!item.ContainedIn.IsValid)
            {
                int dist = _character.Position.GetDistanceTo(item.Position);
                if (dist > 3) { SendPickupFailed(4); return; }
            }
            else
            {
                var topCont = GetTopContainer(item);
                if (topCont != null)
                {
                    if (!topCont.ContainedIn.IsValid)
                    {
                        int cDist = _character.Position.GetDistanceTo(topCont.Position);
                        if (cDist > 3) { SendPickupFailed(4); return; }
                    }
                    else
                    {
                        var wearer = _world.FindChar(topCont.ContainedIn);
                        if (wearer != null && wearer != _character && wearer.IsPlayer)
                        {
                            SendPickupFailed(1); return;
                        }
                        // Self bank box: only reachable while near a banker.
                        if (wearer == _character && topCont.EquipLayer == Layer.BankBox &&
                            !IsNearBanker(_character))
                        {
                            SendPickupFailed(4); return;
                        }
                        if (wearer != null)
                        {
                            int wDist = _character.Position.GetDistanceTo(wearer.Position);
                            if (wDist > 3) { SendPickupFailed(4); return; }
                        }
                    }
                }
            }
        }

        // Looting crime (Source-X CheckCorpseCrime): taking an item out of another
        // player's corpse — when the owner is still present and innocent toward the
        // looter — flags the looter criminal. IsLootingCriminal already exempts the
        // own/party/guild/criminal-owner and deleted-owner (NPC corpse) cases.
        if (_character.PrivLevel < PrivLevel.GM && _client.DeathEng is { } deathEng)
        {
            var lootCorpse = FindEnclosingCorpse(item);
            if (lootCorpse != null && deathEng.IsLootingCriminal(_character, lootCorpse))
                _character.MakeCriminal();
        }

        // Stack splitting: the client keeps dragging the serial it clicked.
        // Source-X/ServUO reduce that original item to the lifted amount and
        // create a new leftover stack at the old location/container.
        if (amount > 0 && amount < item.Amount && item.Amount > 1)
        {
            ushort originalAmount = item.Amount;
            ushort remainderAmount = (ushort)(originalAmount - amount);
            var sourceContainer = item.ContainedIn.IsValid ? _world.FindItem(item.ContainedIn) : null;
            var sourcePos = item.Position;

            // The left-behind remainder must be a full clone of the original
            // (tags, attributes, durability, price/link, timers, TDATA), not just
            // id/hue/more — otherwise that state is lost from the leftover stack.
            var remainder = _world.CreateItem();
            remainder.CopyStackInstanceStateFrom(item);
            remainder.Amount = remainderAmount;

            item.Amount = amount;

            if (sourceContainer != null)
            {
                sourceContainer.RemoveItem(item);
                sourceContainer.AddItem(remainder);
                remainder.Position = sourcePos;
                _netState.Send(new PacketContainerItem(
                    remainder.Uid.Value, remainder.DispIdFull, 0,
                    remainder.Amount, remainder.X, remainder.Y,
                    sourceContainer.Uid.Value, remainder.Hue,
                    _netState.IsClientPost6017));
            }
            else
            {
                var sector = _world.GetSector(sourcePos);
                sector?.RemoveItem(item);
                _world.PlaceItemWithDecay(remainder, sourcePos);
                BroadcastWorldItem(remainder);
            }

            item.ContainedIn = _character.Uid;
            _character.SetTag("DRAGGING", item.Uid.Value.ToString());
            BroadcastDragAnimation(item, dragSourceSerial, dragSourcePos, 0, _character.Position, dragSourcePos);
            return;
        }

        if (item.IsEquipped)
        {
            var owner = _world.FindChar(item.ContainedIn);
            if (owner != null && owner != _character && _character.PrivLevel < PrivLevel.GM)
            {
                SendPickupFailed(1); // cannot pick up
                return;
            }
            // Fire @Unequip trigger on the item being removed
            if (_triggerDispatcher != null && owner != null)
            {
                var unequipResult = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Unequip,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                if (unequipResult == TriggerResult.True)
                {
                    SendPickupFailed(1);
                    return;
                }
            }
            var unequipOwner = owner;
            owner?.Unequip(item.EquipLayer);
            if (unequipOwner != null)
            {
                var removePkt = new PacketDeleteObject(item.Uid.Value);
                BroadcastNearby?.Invoke(unequipOwner.Position, UpdateRange, removePkt, _character.Uid.Value);
            }
        }
        else if (item.ContainedIn.IsValid)
        {
            var container = _world.FindItem(item.ContainedIn);
            container?.RemoveItem(item);
        }
        else
        {
            var sector = _world.GetSector(item.Position);
            sector?.RemoveItem(item);
        }

        item.ContainedIn = _character.Uid;
        _character.SetTag("DRAGGING", serial.ToString());
        BroadcastDragAnimation(item, dragSourceSerial, dragSourcePos, 0, _character.Position, dragSourcePos);

        if (item.BaseId == 0x0EED)
            SendCharacterStatus(_character);
    }

    private (uint Serial, Point3D Position) GetDragSource(Item item)
    {
        if (!item.ContainedIn.IsValid)
            return (0, item.Position);

        var container = _world.FindItem(item.ContainedIn);
        if (container != null)
        {
            var top = GetTopContainer(container) ?? container;
            if (top.ContainedIn.IsValid)
            {
                var holder = _world.FindChar(top.ContainedIn);
                if (holder != null)
                    return (holder.Uid.Value, holder.Position);
            }
            return (top.Uid.Value, top.Position);
        }

        var character = _world.FindChar(item.ContainedIn);
        if (character != null)
            return (character.Uid.Value, character.Position);

        return (0, item.Position);
    }

    // === Source-X drag bounce-to-origin (CClientEvent pickup origin) ===

    /// <summary>Lift origin for the item currently being dragged. Held as
    /// transient per-client state (one drag at a time) rather than item tags —
    /// tags would pollute the item's tag set and break stack-merge equality.</summary>
    private enum DragOriginKind : byte { Pack = 0, Container = 1, Ground = 2 }
    private readonly record struct DragOrigin(DragOriginKind Kind, uint Parent, short X, short Y, sbyte Z);
    /// <summary>
    /// Whether a drop target can legitimately hold children. Anything that is not a
    /// container is a plain item, however the client addressed it.
    /// </summary>
    private static bool IsDropTargetContainer(Item target) =>
        target.ItemType is ItemType.Container or ItemType.ContainerLocked or
            ItemType.Corpse or ItemType.EqBankBox or ItemType.EqVendorBox or
            ItemType.EqTradeWindow or ItemType.Spellbook or ItemType.EqMemoryObj ||
        target.EquipLayer == Layer.Pack || target.EquipLayer == Layer.BankBox ||
        target.EquipLayer == Layer.VendorStock || target.EquipLayer == Layer.VendorExtra;

    /// <summary>
    /// Resolve a drop aimed at a plain item to the place that item actually lives
    /// (Source-X CClientEvent.cpp:504) - the container holding it, or its ground
    /// tile. Returns null to mean "put it on the ground", with the coordinates
    /// updated to the target's tile.
    /// </summary>
    private Item? RedirectNonContainerTarget(Item target, Item item, ref short x, ref short y)
    {
        var parent = target.ContainedIn.IsValid ? _world.FindItem(target.ContainedIn) : null;
        if (parent != null && !ReferenceEquals(parent, item))
        {
            // Land next to the target inside its own container.
            x = target.X;
            y = target.Y;
            return parent;
        }

        var pos = target.GetTopLevelPosition();
        x = pos.X;
        y = pos.Y;
        return null;   // ground drop at the target's tile
    }

    /// <summary>
    /// Whether this client is allowed to take <paramref name="item"/> out of
    /// whatever holds it. Items on the ground, worn, or held by the character
    /// itself are not container pickups and pass straight through.
    /// </summary>
    private bool CanReachInsideContainer(Item item)
    {
        if (_character == null) return false;
        if (_character.PrivLevel >= PrivLevel.GM) return true;
        if (item.IsEquipped || !item.ContainedIn.IsValid) return true;

        var container = _world.FindItem(item.ContainedIn);
        if (container == null) return true;   // held by a character, not a container

        var topMost = container.ResolveTopObject();
        bool topIsCharacter = topMost is Character;

        // The character's own pack and bank are always reachable: the client is
        // never sent an "open" for its own backpack on every login, and Source-X
        // treats a pickup whose top-level object is a character as legitimate.
        if (topIsCharacter && ReferenceEquals(topMost, _character))
            return true;

        return _client.OpenedContainers.IsOpen(container, topMost, topIsCharacter);
    }

    private DragOrigin? _dragOrigin;

    /// <summary>
    /// Settle whatever is already on the cursor before a new pickup takes it over.
    /// Returns false when the new request should be dropped entirely (it is a repeat
    /// of the item already being dragged, so there is nothing left to do).
    /// </summary>
    private bool ResolvePreviousDrag(Item incoming)
    {
        if (_character == null) return true;
        if (!_character.TryGetTag("DRAGGING", out string? raw) ||
            !uint.TryParse(raw, out uint heldUid) || heldUid == 0)
            return true;

        // Same item twice: Source-X ItemPickup returns early rather than restarting
        // the drag, so the origin captured by the first pickup survives.
        if (heldUid == incoming.Uid.Value)
        {
            SendPickupFailed(0);
            return false;
        }

        _character.RemoveTag("DRAGGING");

        var held = _world.FindItem(new Serial(heldUid));
        if (held is { IsDeleted: false })
            RestoreToOrigin(held);   // consumes and clears _dragOrigin

        _dragOrigin = null;
        return true;
    }

    /// <summary>
    /// Snapshot where an item is being lifted FROM so a failed drop can bounce it
    /// back to that container slot / ground tile (Source-X stores the prior parent
    /// + position), not just the backpack. Overwritten on every pickup, so it
    /// can't go stale — a drop only follows the pickup that just set it.
    /// </summary>
    private void CaptureDragOrigin(Item item)
    {
        if (item.IsEquipped)
            // Equip-origin bounces to the pack: re-equipping on the failure path
            // would re-fire @EquipTest / hand-conflict logic.
            _dragOrigin = new DragOrigin(DragOriginKind.Pack, 0, 0, 0, 0);
        else if (item.ContainedIn.IsValid && _world.FindItem(item.ContainedIn) != null)
            _dragOrigin = new DragOrigin(DragOriginKind.Container, item.ContainedIn.Value, item.X, item.Y, 0);
        else
            _dragOrigin = new DragOrigin(DragOriginKind.Ground, 0, item.X, item.Y, item.Z);
    }

    /// <summary>
    /// Bounce a failed drop back to its lift origin — the original container slot
    /// or ground tile — and send the matching client update. Falls back to the
    /// backpack when the origin is gone, full, or was an equip layer. Mirrors
    /// PlaceItemInPack's reparent + 0x25, but targets the origin.
    /// </summary>
    private void RestoreToOrigin(Item item)
    {
        if (_character == null) return;
        var origin = _dragOrigin;
        _dragOrigin = null;

        if (origin is { Kind: DragOriginKind.Container } co)
        {
            var originCont = _world.FindItem(new Serial(co.Parent));
            // The item was a leaf of this container moments ago, so there is room
            // and no cycle; guard only against a deleted/full origin.
            if (originCont != null && !originCont.IsDeleted &&
                _world.GetContainerContents(originCont.Uid).Count() < Item.MaxContainerItems)
            {
                originCont.AddItem(item);
                item.Position = new Point3D(co.X, co.Y, 0, _character.MapIndex);
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0, item.Amount,
                    co.X, co.Y, originCont.Uid.Value, item.Hue, _netState.IsClientPost6017));
                return;
            }
        }
        else if (origin is { Kind: DragOriginKind.Ground } go)
        {
            var pos = new Point3D(go.X, go.Y, go.Z, _character.MapIndex);
            if (_world.GetSector(pos) != null)
            {
                _world.PlaceItemWithDecay(item, pos);
                BroadcastWorldItem(item);
                return;
            }
        }

        PlaceItemInPack(_character, item);
    }

    private void BroadcastDragAnimation(Item item, uint sourceSerial, Point3D sourcePos,
        uint targetSerial, Point3D targetPos, Point3D origin)
    {
        var packet = new PacketDragAnimation(
            item.DispIdFull,
            item.Hue,
            item.Amount == 0 ? (ushort)1 : item.Amount,
            sourceSerial,
            sourcePos.X,
            sourcePos.Y,
            sourcePos.Z,
            targetSerial,
            targetPos.X,
            targetPos.Y,
            targetPos.Z);

        if (ForEachClientInRange != null)
        {
            ForEachClientInRange(origin, UpdateRange, 0, (_, observer) =>
            {
                if (observer.NetState.IsKingdomRebornClient
                    || observer.NetState.IsEnhancedClient
                    || observer.NetState.SupportsStygianAbyss)
                    return;

                observer.Send(packet);
            });
            return;
        }

        BroadcastNearby?.Invoke(origin, UpdateRange, packet, 0);
    }

    // ==================== Item Drop ====================

    public void HandleItemDrop(uint serial, short x, short y, sbyte z, uint containerUid)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        if (!_character.TryGetTag("DRAGGING", out var dragTag) || dragTag != serial.ToString())
        {
            _netState.Send(new PacketDropReject());
            return;
        }

        // Symmetric with HandleItemPickup, and with Source-X CanTouch, which refuses
        // a dead character every item that is not death-immune. Death now settles any
        // held item into the pack before the corpse is filled, so a ghost arriving
        // here is a stale client-side drag rather than a legitimate move.
        if (_character.IsDead)
        {
            _character.RemoveTag("DRAGGING");
            RestoreToOrigin(item);
            _netState.Send(new PacketDropReject());
            return;
        }

        _character.RemoveTag("DRAGGING");

        if (containerUid != 0 && containerUid != 0xFFFFFFFF)
        {
            var container = _world.FindItem(new Serial(containerUid));

            // Source-X Event_Item_Drop (CClientEvent.cpp:489) branches on whether the
            // target really is a container. A plain item is NOT one: the drop is
            // redirected to wherever that item lives, after the stack-merge and
            // special-target cases below have had their chance. Passing it to
            // TryAddItem instead turned a sword into a container, and the gem inside
            // it could not be reached through any container view again.
            if (container != null && !IsDropTargetContainer(container) &&
                !container.CanStackWith(item))
            {
                container = RedirectNonContainerTarget(container, item, ref x, ref y);
            }

            if (container != null && _tradeManager?.FindByContainer(container.Uid.Value) is { } dropTrade)
            {
                if (!dropTrade.IsParticipant(_character))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                var dropOnTradeResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.DropOnTrade,
                    new TriggerArgs
                    {
                        CharSrc = _character,
                        ItemSrc = item,
                        O1 = dropTrade.GetPartner(_character),
                        N1 = (int)dropTrade.SessionId.Value
                    });
                if (dropOnTradeResult == TriggerResult.True)
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                var myCont = dropTrade.GetOwnContainer(_character);
                if (!myCont.TryAddItem(item))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
                item.Position = new Point3D(30, 30, 0, _character.MapIndex);
                dropTrade.ResetAcceptance();
                SendTradeUpdateToBoth(dropTrade);
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, 30, 30,
                    myCont.Uid.Value, item.Hue, _netState.IsClientPost6017));
                SendTradeItemToPartner?.Invoke(dropTrade.GetPartner(_character), item, myCont);
                _netState.Send(new PacketDropAck());
                return;
            }
            if (container != null)
            {
                // Distance check: player must be near the container (or its parent on world).
                // The self pack is always reachable; the self bank box only while near a
                // banker; another player's pack/bank never; a world container within 3 tiles.
                // Without this a crafted packet can move items into distant containers the
                // client happened to open earlier.
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    if (IsInSelfBankBox(_character, container))
                    {
                        // Self bank box (direct or via a nested bag) — re-check banker
                        // proximity; the box was opened at one.
                        if (!IsNearBanker(_character))
                        {
                            RestoreToOrigin(item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                    else
                    {
                        // Resolve the ROOT of the target chain, not just the layer of
                        // the container the client named. Checking only a direct
                        // Pack/BankBox layer meant a bag nested inside another
                        // player's backpack fell through to the distance branch,
                        // which does not apply to a character-held container - so an
                        // item could be pushed straight into someone else's
                        // inventory, bypassing the secure-trade flow Source-X routes
                        // it to (CClientEvent.cpp:338).
                        var topContainer = GetTopContainer(container) ?? container;
                        var rootOwner = topContainer.ContainedIn.IsValid
                            ? _world.FindChar(topContainer.ContainedIn)
                            : null;

                        if (rootOwner != null && rootOwner != _character)
                        {
                            // Someone else's inventory (or their pet's). Handing an
                            // item over needs their consent.
                            if (!rootOwner.HasOwner(_character.Uid))
                            {
                                RestoreToOrigin(item);
                                _netState.Send(new PacketDropReject());
                                return;
                            }
                        }
                        else if (rootOwner == null && !topContainer.ContainedIn.IsValid)
                        {
                            int cDist = _character.Position.GetDistanceTo(topContainer.Position);
                            if (cDist > 3)
                            {
                                RestoreToOrigin(item);
                                _netState.Send(new PacketDropReject());
                                return;
                            }
                        }
                    }
                }

                // When the "container" is actually a matching stackable pile, this
                // drop is a MERGE, not an insert into a container. Source-X does not
                // gate a pile merge by the destination's item-count / weight / nesting
                // budget (the pile just grows up to GetMaxAmount) — those container
                // caps only apply when dropping a distinct item into a container slot.
                // Applying MaxContainerWeight to a ground pile wrongly rejected large
                // stacks (e.g. dropping 5000 gold — 500 stones — onto a ground pile
                // when the container weight cap is 400).
                bool isPileMerge = container.CanStackWith(item);

                // Nesting depth limit — prevent container-in-container bypass of slot limits.
                if (!isPileMerge && _character.PrivLevel < PrivLevel.GM)
                {
                    int depth = 0;
                    var parent = container;
                    while (parent != null && parent.ContainedIn.IsValid && depth < 8)
                    {
                        parent = _world.FindItem(parent.ContainedIn);
                        depth++;
                    }
                    if (depth >= 8)
                    {
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }

                if (!isPileMerge && _character.PrivLevel < PrivLevel.GM)
                {
                    // A drop anywhere in the bank tree (the box itself or a nested
                    // bag) counts against the bank cap, computed over the WHOLE
                    // tree from the bank root so nested bags can't bypass the limit
                    // (Source-X). A normal container counts only its own slot.
                    var topContainer = GetTopContainer(container) ?? container;
                    bool isBank = topContainer.EquipLayer == Layer.BankBox;
                    int currentCount = isBank
                        ? _world.GetContainerItemCountDeep(topContainer.Uid)
                        : _world.GetContainerContents(container.Uid).Count();
                    int maxItems = isBank ? _world.MaxBankItems : _world.MaxContainerItems;
                    // Source-X OVERRIDE.MAXITEMS: a per-container item cap (e.g. a
                    // small pouch, a quest box, a vendor crate) overrides the
                    // global default for THIS container.
                    if (container.TryGetTag("OVERRIDE.MAXITEMS", out string? maxItemsRaw) &&
                        int.TryParse(maxItemsRaw, out int overrideMax) && overrideMax >= 0)
                        maxItems = overrideMax;
                    // Source-X CItemContainer::CanContainerHold adds the INCOMING
                    // container's children to the bank's own count
                    // (CItemContainer.cpp:941: ContentCountAll() + iItemsInContainer).
                    // Counting only what was already in the bank let a player pack
                    // items into a bag first and walk the bag past the item limit.
                    // The check is bank-specific in the reference: a normal container
                    // caps its own slots, not the depth of what goes into one.
                    int incomingChildren = isBank && item.ContentCount > 0
                        ? _world.GetContainerItemCountDeep(item.Uid)
                        : 0;

                    if (currentCount + incomingChildren >= maxItems)
                    {
                        SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullItems : Msg.ContFullItems));
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                    int weightLimit = isBank ? _world.MaxBankWeight : _world.MaxContainerWeight;
                    if (weightLimit > 0)
                    {
                        int totalWeightTenths = 0;
                        foreach (var b in _world.GetContainerContents(container.Uid))
                            totalWeightTenths += b.TotalWeightTenths;
                        if (totalWeightTenths + item.TotalWeightTenths > weightLimit * Item.WeightUnits)
                        {
                            SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullWeight : Msg.ContFullWeight));
                            RestoreToOrigin(item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                }

                if (item.Uid == container.Uid || IsInsideContainer(container, item.Uid))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                // Fire @DropOn_Item
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = container });
                    if (result == TriggerResult.True)
                    {
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }
                // Dropping one stack onto another: the client sends the TARGET
                // STACK's serial as the "container". If that target is itself a
                // stackable item that matches, MERGE the amounts instead of
                // nesting the dragged item inside the stack — nesting made the
                // dropped pile a hidden child of the target and it silently
                // vanished (the "stack gold onto gold and 10k disappears" bug).
                if (isPileMerge)
                {
                    // A target stack on the ground is a world object, not a
                    // container child: its amount update must go out as a world
                    // item broadcast (0x1A), not a container-content packet
                    // (0x25) addressed to itself. Sending 0x25 with parent ==
                    // the item's own serial desynced the ground pile's label.
                    bool targetOnGround = !container.ContainedIn.IsValid;
                    uint stackParent = container.ContainedIn.IsValid
                        ? container.ContainedIn.Value : container.Uid.Value;
                    // Cap the merge at the target's effective stack limit
                    // (per-item MAXAMOUNT override or ITEMSMAXAMOUNT, Source-X
                    // GetMaxAmount), not the raw ushort ceiling — otherwise a pile
                    // grows past the configured/per-item cap. The other two merge
                    // sites (Item.TryAddItemWithStack, ground-drop merge) already
                    // use MaxAmount.
                    int room = container.MaxAmount - container.Amount;
                    if (room < 0) room = 0;
                    int originalAmount = item.Amount;
                    int moved = Math.Min(room, originalAmount);
                    int remaining = originalAmount - moved;
                    if (moved > 0)
                    {
                        container.Amount = (ushort)(container.Amount + moved);
                        if (targetOnGround)
                            BroadcastWorldItem(container);
                        else
                            _netState.Send(new PacketContainerItem(
                                container.Uid.Value, container.DispIdFull, 0,
                                container.Amount, container.X, container.Y,
                                stackParent, container.Hue, _netState.IsClientPost6017));
                    }
                    if (remaining <= 0)
                    {
                        _world.RemoveItem(item);
                    }
                    else if (container.ContainedIn.IsValid &&
                             _world.FindItem(container.ContainedIn) is { } realParent)
                    {
                        // Overflow remainder stays beside the target stack.
                        item.Amount = (ushort)remaining;
                        if (!realParent.TryAddItem(item))
                        {
                            _world.PlaceItemWithDecay(item, container.GetTopLevelPosition());
                            BroadcastWorldItem(item);
                            _netState.Send(new PacketDropAck());
                            return;
                        }
                        item.Position = new Point3D(container.X, container.Y, 0, _character.MapIndex);
                        _netState.Send(new PacketContainerItem(
                            item.Uid.Value, item.DispIdFull, 0,
                            item.Amount, item.X, item.Y,
                            realParent.Uid.Value, item.Hue, _netState.IsClientPost6017));
                    }
                    else if (targetOnGround)
                    {
                        // Overflow remainder drops to the ground beside the
                        // target pile so it is never lost.
                        item.Amount = (ushort)remaining;
                        _world.PlaceItemWithDecay(item, container.Position);
                        BroadcastWorldItem(item);
                    }
                    else
                    {
                        // Contained target whose parent lookup failed (a child that
                        // outlived its parent — torn world state). Without this the
                        // source kept its full Amount while the target already grew
                        // by `moved`, duplicating `moved` units. Reduce the source to
                        // the remainder and bounce it to the target's top-level tile.
                        item.Amount = (ushort)remaining;
                        _world.PlaceItemWithDecay(item, container.GetTopLevelPosition());
                        BroadcastWorldItem(item);
                    }
                    _netState.Send(new PacketDropAck());
                    return;
                }

                if (!container.TryAddItem(item))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
                item.Position = new Point3D(x, y, 0, _character.MapIndex);
                // Critical: tell the client the item actually landed in the
                // container. Without 0x25 the client only remembers the
                // earlier pickup → the item silently vanishes from its view.
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, item.X, item.Y,
                    container.Uid.Value, item.Hue,
                    _netState.IsClientPost6017));
                _netState.Send(new PacketDropAck());
                if (item.BaseId == 0x0EED)
                    SendCharacterStatus(_character);
                return;
            }

            var charTarget = _world.FindChar(new Serial(containerUid));
            if (charTarget != null && charTarget == _character)
            {
                // Fire @DropOn_Self
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnSelf,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                }
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }
            else if (charTarget != null)
            {
                if (_character.PrivLevel < PrivLevel.GM &&
                    (_character.MapIndex != charTarget.MapIndex ||
                     _character.Position.GetDistanceTo(charTarget.Position) > 3))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                // Fire @DropOn_Char
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnChar,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = charTarget });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                }

                if (charTarget.IsPlayer && _tradeManager != null)
                {
                    // Source-X Event_Item_Drop bounces the item when Cmd_SecureTrade
                    // refuses (CClientEvent.cpp:325). Acking a drop that never landed
                    // told the client it had succeeded while the item sat parented to
                    // the character, out of reach of every inventory view.
                    if (!InitiateTrade(charTarget, item))
                    {
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // Source-X NPC_OnItemGive: @ReceiveItem fires FIRST — a
                // quest/reward script may fully consume the gift before ANY
                // native handling, the train/hire gold pay included.
                if (!charTarget.IsPlayer && _triggerDispatcher != null &&
                    _triggerDispatcher.FireCharTrigger(charTarget, CharTrigger.ReceiveItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item })
                    == TriggerResult.True)
                {
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // Source-X NPC_OnTrainPay: gold handed to a trainer with a pending
                // "train <skill>" offer buys skill points — 1 gp per 0.1, capped
                // at the trainer's limit. Leftover gold bounces back.
                if (!charTarget.IsPlayer &&
                    (item.ItemType == ItemType.Gold || item.BaseId == 0x0EED) &&
                    _character.TryGetTag("TRAIN_PENDING", out string? trainPending) &&
                    TryApplyTrainPayment(charTarget, item, trainPending!))
                {
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // Source-X NPC_OnHirePay: gold handed to a hireable NPC
                // (chardef HIREDAYWAGE) hires it and/or extends its PREPAID
                // wage balance — the hireling later drains this balance per
                // period (NPC_CheckHirelingStatus), never the master's bank.
                if (!charTarget.IsPlayer &&
                    (item.ItemType == ItemType.Gold || item.BaseId == 0x0EED) &&
                    TryApplyHirePayment(charTarget, item))
                {
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // Source-X NPC_OnTrainPay: gold handed to a teacher NPC with a
                // pending training offer buys skill points. Any change stays with
                // the player's gold stack (TryPay trims the amount in place).
                if (!charTarget.IsPlayer && _character != null &&
                    (item.ItemType == ItemType.Gold || item.BaseId == 0x0EED))
                {
                    var trained = Trade.VendorTrainingEngine.TryPay(charTarget, _character, item);
                    if (trained != null)
                    {
                        SysMessage($"Thou hast learned something of {trained}.");
                        SendSkillList();
                        if (!item.IsDeleted)
                            PlaceItemInPack(_character!, item); // return the change
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                }

                // Native give flow (Source-X NPC_OnItemGive tail).
                if (!charTarget.IsPlayer)
                {
                    HandleGiveItemToNpc(charTarget, item);
                    return;
                }

                PlaceItemInPack(charTarget, item);
                _netState.Send(new PacketDropAck());
                return;
            }
        }

        // Distance check + map bounds check for ground drops
        if (_character.PrivLevel < PrivLevel.GM)
        {
            var md = _world.MapData;
            if (md != null)
            {
                var (mapW, mapH) = md.GetMapSize(_character.MapIndex);
                if (x < 0 || y < 0 || x >= mapW || y >= mapH)
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
            }
            int dropDist = Math.Max(Math.Abs(_character.X - x), Math.Abs(_character.Y - y));
            if (dropDist > 3)
            {
                RestoreToOrigin(item);
                _netState.Send(new PacketDropReject());
                return;
            }
        }

        if (_character.PrivLevel < PrivLevel.GM && _housingEngine != null)
        {
            var dropPos = new Point3D(x, y, z, _character.MapIndex);
            var house = _housingEngine.FindHouseAt(dropPos);
            if (house != null && !house.CanAccess(_character.Uid))
            {
                RestoreToOrigin(item);
                _netState.Send(new PacketDropReject());
                return;
            }
        }

        // @DropOn_Ground — Source-X passes the drop point as ARGN1/2/3 (x/y/z)
        // and a DECAY local (seconds) so a script can relocate the drop or set a
        // custom rot timer; RETURN 1 bounces the drop. ARGN/LOCAL readback flows
        // through the EVENTS / @Item* path (RunWrapped + the shared Locals pool).
        long defaultDecaySec = GameWorld.DefaultDecayTimeMs / 1000;
        var dropLocals = new SphereNet.Scripting.Variables.VarMap();
        dropLocals.SetInt("DECAY", defaultDecaySec);
        var dropArgs = new TriggerArgs
        {
            CharSrc = _character, ItemSrc = item,
            N1 = x, N2 = y, N3 = z, Locals = dropLocals,
        };
        var dropResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.DropOnGround, dropArgs);
        if (dropResult == TriggerResult.True)
        {
            RestoreToOrigin(item);
            _netState.Send(new PacketDropReject());
            return;
        }

        // Honor a script-relocated drop point, re-validating map bounds + reach so
        // a script can't fling the item out of the world / out of range (GM keeps
        // the same bypass the pre-trigger gate used).
        short finalX = (short)dropArgs.N1, finalY = (short)dropArgs.N2;
        sbyte finalZ = (sbyte)dropArgs.N3;
        if (finalX != x || finalY != y || finalZ != z)
        {
            bool inBounds = true;
            var relocMd = _world.MapData;
            if (relocMd != null)
            {
                var (mw, mh) = relocMd.GetMapSize(_character.MapIndex);
                inBounds = finalX >= 0 && finalY >= 0 && finalX < mw && finalY < mh;
            }
            int relocDist = Math.Max(Math.Abs(_character.X - finalX), Math.Abs(_character.Y - finalY));
            if (inBounds && (_character.PrivLevel >= PrivLevel.GM || relocDist <= 3))
            {
                x = finalX; y = finalY; z = finalZ;
            }
        }

        // Script decay override (LOCAL.DECAY seconds): >0 sets a custom timer,
        // otherwise the default 10-minute ground decay applies.
        long dropDecaySec = dropLocals.GetInt("DECAY", defaultDecaySec);
        long dropDecayMs = dropDecaySec > 0 ? dropDecaySec * 1000 : GameWorld.DefaultDecayTimeMs;

        var groundPos = new Point3D(x, y, z, _character.MapIndex);
        var sector = _world.GetSector(groundPos);

        // Stack onto a matching pile if possible; otherwise count the non-stacking
        // items already on this exact tile so the new drop gets a distinct facing.
        // A pile of separate items should scatter (Source-X behaviour) rather than
        // all share one orientation — basing direction on the item's own previous
        // value made every fresh drop land as Direction=1.
        int tileItemCount = 0;
        if (sector != null)
        {
            foreach (var existing in sector.Items)
            {
                if (existing.X != x || existing.Y != y || existing.MapIndex != _character.MapIndex)
                    continue;

                if (existing.CanStackWith(item) && (existing.Amount + item.Amount) <= existing.MaxAmount)
                {
                    existing.Amount += item.Amount;
                    if (existing.IsPile)
                        existing.Direction = 0;
                    BroadcastDragAnimation(item, _character.Uid.Value, _character.Position, 0, existing.Position, existing.Position);
                    _world.RemoveItem(item);
                    _netState.Send(new PacketDropAck());
                    BroadcastNearby?.Invoke(existing.Position, UpdateRange,
                        new PacketSound(GetDropSound(existing), existing.X, existing.Y, existing.Z), 0);
                    BroadcastWorldItem(existing);
                    return;
                }

                tileItemCount++;
            }
        }

        if (item.IsPile)
        {
            item.Direction = 0;
        }
        else
        {
            item.Direction = (byte)((tileItemCount % 7) + 1);
            item.TryFlipDisplay();
        }

        _world.PlaceItemWithDecay(item, groundPos, dropDecayMs);
        _netState.Send(new PacketDropAck());
        BroadcastDragAnimation(item, _character.Uid.Value, _character.Position, 0, groundPos, groundPos);
        BroadcastNearby?.Invoke(groundPos, UpdateRange,
            new PacketSound(GetDropSound(item), groundPos.X, groundPos.Y, groundPos.Z), 0);
        BroadcastWorldItem(item);
    }

    /// <summary>
    /// Script BOUNCE/DROP verb bridge (Character.OnDragRelease): release the
    /// item the character is dragging (DRAGGING tag) either to the ground at
    /// their feet or back into the backpack, and cancel the client-side drag
    /// cursor with 0x27. Returns false when nothing is being dragged.
    /// </summary>
    /// <summary>Tell the client to put the drag cursor down. The item itself is the
    /// caller's business — death takes it and applies the equipment rules.</summary>
    public void CancelDragCursor() => _netState.Send(new PacketPickupFailed(0));

    public bool ReleaseDraggedItem(bool toGround)
    {
        if (_character == null)
            return false;
        if (!_character.TryGetTag("DRAGGING", out string? dragSer) ||
            !uint.TryParse(dragSer, out uint dragUid))
            return false;

        _character.RemoveTag("DRAGGING");
        _netState.Send(new PacketPickupFailed(0)); // cancel the drag cursor

        var item = _world.FindItem(new Serial(dragUid));
        if (item == null || item.IsDeleted)
            return true;

        var pack = _character.Backpack;
        if (!toGround && pack != null)
        {
            if (!pack.TryAddItem(item))
            {
                _world.PlaceItemWithDecay(item, _character.Position);
                BroadcastWorldItem(item);
                return true;
            }
            item.Position = new Point3D(50, 50, 0, _character.MapIndex);
            _netState.Send(new PacketContainerItem(
                item.Uid.Value, item.DispIdFull, 0, item.Amount,
                item.X, item.Y, pack.Uid.Value, item.Hue,
                _netState.IsClientPost6017));
            return true;
        }

        _world.PlaceItemWithDecay(item, _character.Position);
        BroadcastWorldItem(item);
        return true;
    }

    /// <summary>Item-aware drop sound: gold coins get the amount-scaled coin
    /// sounds; everything else keeps the generic 0x42 item drop.</summary>
    private static ushort GetDropSound(Item item)
    {
        if (item.BaseId == 0x0EED)
            return item.Amount switch { 1 => (ushort)0x02E4, < 6 => (ushort)0x02E5, _ => (ushort)0x02E6 };
        return 0x0042;
    }

    // ==================== Item Equip ====================

    /// <summary>Equip-first double-click step (Source-X CClient::Cmd_Use_Item):
    /// an equippable item that is not currently equipped is armed/worn when
    /// double-clicked — from the ground or a container — before its use-type
    /// behavior runs. Routes through the real pickup + equip paths so reach,
    /// ownership, @PickUp_*/@EquipTest/@Equip triggers and hand-swap rules all
    /// apply; the current occupant of the layer is bounced to the backpack.
    /// Returns true when the item ended up equipped.</summary>
    public bool TryDClickEquip(Item item, Layer layer)
    {
        if (_character == null || item.IsEquipped)
            return false;
        if (layer <= Layer.None || layer >= Layer.Qty)
            return false;

        var prev = _character.GetEquippedItem(layer);

        // Lift through the real pickup path (reach, housing, trigger gates).
        HandleItemPickup(item.Uid.Value, 0);
        if (!_character.TryGetTag("DRAGGING", out string? dragStr) ||
            dragStr != item.Uid.Value.ToString())
            return false;
        _character.RemoveTag("DRAGGING");

        // Bounce the layer's current occupant to the pack before equipping —
        // Character.Equip alone would orphan it (Source-X ItemEquip bounce).
        if (prev != null && _character.GetEquippedItem(layer) == prev)
        {
            _character.Unequip(layer);
            var removePkt = new PacketDeleteObject(prev.Uid.Value);
            _netState.Send(removePkt);
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, removePkt, _character.Uid.Value);
            PlaceItemInPack(_character, prev);
        }

        HandleItemEquip(item.Uid.Value, (byte)layer, _character.Uid.Value);
        return item.IsEquipped;
    }

    /// <summary>0xEC equip-item macro (Source-X PacketEquipItemMacro). Each
    /// serial must already be carried by the character and not worn; the item
    /// is lifted and equipped on its own layer, so reach checks and the
    /// @PickUp_*/@EquipTest/@Equip triggers all still run. Serials that fail
    /// any step are skipped rather than aborting the batch, as upstream does.
    /// </summary>
    public void HandleEquipMacro(IReadOnlyList<uint> serials)
    {
        if (_character == null || _character.IsDead) return;

        foreach (uint serial in serials)
        {
            var item = _world.FindItem(new Serial(serial));
            if (item == null || item.IsEquipped)
                continue;
            // Source-X requires the item's top-level owner to be the character:
            // the macro equips from your own pack, never off the ground.
            if (item.ResolveTopObject() != _character)
                continue;

            Layer layer = _client.ItemUse.ResolveWearableLayer(item);
            if (layer is Layer.None or Layer.Pack or Layer.Hair or Layer.FacialHair ||
                (int)layer >= (int)Layer.Horse)
                continue;

            TryDClickEquip(item, layer);
        }
    }

    /// <summary>0xED unequip-item macro (Source-X PacketUnEquipItemMacro):
    /// strip the named layers back into the pack.</summary>
    public void HandleUnequipMacro(IReadOnlyList<ushort> layers)
    {
        if (_character == null || _character.IsDead) return;

        foreach (ushort raw in layers)
        {
            var layer = (Layer)raw;
            if (layer is Layer.None or Layer.Pack or Layer.Hair or Layer.FacialHair ||
                (int)layer >= (int)Layer.Horse)
                continue;

            var item = _character.GetEquippedItem(layer);
            if (item == null || !ItemMoveRules.CanMove(_character, item, out _))
                continue;

            _character.Unequip(layer);
            var removePkt = new PacketDeleteObject(item.Uid.Value);
            _netState.Send(removePkt);
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, removePkt, _character.Uid.Value);
            PlaceItemInPack(_character, item);
        }
    }

    public void HandleItemEquip(uint serial, byte layer, uint charSerial)
    {
        if (_character == null) return;
        if (_character.IsDead) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        // Reject the internal-only dragging/sentinel layers (31+). Dragging is
        // not a client-equippable slot; this preserves the prior bound now that
        // Layer.Dragging sits between Special and Qty.
        if (layer == 0 || layer >= (byte)Layer.Dragging) return;

        if (_character.PrivLevel < PrivLevel.GM &&
            item.ContainedIn != _character.Uid)
            return;

        var target = _world.FindChar(new Serial(charSerial));
        if (target == null) target = _character;

        if (target != _character && _character.PrivLevel < PrivLevel.GM) return;

        // Fire @EquipTest — if script blocks, deny equip
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.EquipTest,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            if (result == TriggerResult.True)
                return;
        }

        // Central equip gate (Source-X CChar::CanEquipLayer): block an
        // underpowered wearer from a high-REQSTR item. GM actor bypasses; the
        // layer-31 / ownership / hand-conflict guards are handled separately.
        if (_character.PrivLevel < PrivLevel.GM &&
            !target.CanEquip(item, (Layer)layer, out var equipDenial))
        {
            if (equipDenial == Character.EquipDenial.TooWeak)
                SysMessage("You are not strong enough to equip that.");
            return;
        }

        // Spell interruption on equip change
        _spellEngine?.TryInterruptFromEquip(target);

        // Hands mutual exclusion (UO rules): a true two-handed weapon needs
        // BOTH hands, so it bounces whatever the other hand holds — weapon or
        // shield. Conversely any one-hand-layer equip bounces a held
        // two-handed weapon (it coexists with a shield, which is not
        // two-handed). The old check only covered the shield cases, so a
        // 1H weapon + 2H weapon could end up held together.
        if ((Layer)layer == Layer.TwoHanded && item.IsTwoHanded)
        {
            var offhand = target.GetEquippedItem(Layer.OneHanded);
            if (offhand != null)
            {
                target.Unequip(Layer.OneHanded);
                PlaceItemInPack(target, offhand);
            }
        }
        else if ((Layer)layer == Layer.OneHanded)
        {
            var oldWeapon = target.GetEquippedItem(Layer.TwoHanded);
            if (oldWeapon != null && oldWeapon.IsTwoHanded)
            {
                target.Unequip(Layer.TwoHanded);
                PlaceItemInPack(target, oldWeapon);
            }
        }

        target.Equip(item, (Layer)layer);
        // Equip may promote a two-handed weapon from the OneHanded layer to
        // TwoHanded; reflect the actual layer to the client so it renders/animates
        // the weapon correctly (a bow on the wrong layer animates as a punch).
        byte actualLayer = (byte)item.EquipLayer;
        if (item.EquipLayer is Layer.OneHanded or Layer.TwoHanded && IsCombatEquipItem(item))
        {
            bool noWait = (Character.CombatFlags & (int)CombatFlags.FirstHitInstant) != 0;
            int delayMs = CombatEngine.GetSwingDelayMs(target, item);
            target.BeginEquipSwingWait(Environment.TickCount64, delayMs, noWait);
        }

        var wornPkt = new PacketWornItem(
            item.Uid.Value, item.DispIdFull, actualLayer,
            target.Uid.Value, item.Hue);
        _netState.Send(wornPkt);
        BroadcastNearby?.Invoke(target.Position, UpdateRange, wornPkt, _character.Uid.Value);

        _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Equip,
            new TriggerArgs { CharSrc = _character, ItemSrc = item });
    }

    private static bool IsCombatEquipItem(Item item) => item.ItemType is
        ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or ItemType.WeaponSword or
        ItemType.WeaponFence or ItemType.WeaponBow or ItemType.WeaponMaceStaff or
        ItemType.WeaponMaceCrook or ItemType.WeaponMacePick or ItemType.WeaponAxe or
        ItemType.WeaponXBow or ItemType.WeaponThrowing or ItemType.WeaponWhip or
        ItemType.Shield;

    // ==================== Status Request ====================

    public void HandleProfileRequest(byte mode, uint serial, string bioText = "")
    {
        if (_character == null) return;

        Character? ch = _world.FindChar(new Serial(serial));
        ch ??= _character;

        if (mode == 1)
        {
            if (ch == _character || _character.PrivLevel >= PrivLevel.GM)
            {
                var result = _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Profile, new TriggerArgs
                {
                    CharSrc = _character,
                    S1 = bioText,
                    N1 = mode
                });
                if (result == TriggerResult.True)
                    return;

                ch.SetTag("PROFILE_BIO", bioText);
            }
            return;
        }

        if (_triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Profile, new TriggerArgs
        {
            CharSrc = _character,
            N1 = mode
        }) == TriggerResult.True)
            return;

        string title = string.IsNullOrEmpty(ch.Title)
            ? ch.GetName()
            : $"{ch.GetName()}, {ch.Title}";

        string profile = ch.TryGetTag("PROFILE_BIO", out string? bio) && bio != null ? bio : "";
        _netState.Send(new PacketProfileResponse(ch.Uid.Value, title, profile));
    }

    public void HandleStatusRequest(byte type, uint serial)
    {
        if (_character == null) return;

        if (type == 4 || type == 0) // status
        {
            Character? ch = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
                ch = _world.FindChar(new Serial(serial));

            // Some clients may request status with invalid/empty serial after resync.
            // Fallback to self so status bars are never blank.
            ch ??= _character;

            // Self status is always allowed; other mobiles require visibility/range.
            if (ch != _character && !CanSendStatusFor(ch))
                return;

            // @UserStats fires when the client opens the status window
            // on *its own* character. Matches Source-X CClient::Event_StatusRequest.
            if (ch == _character)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserStats,
                    new TriggerArgs { CharSrc = _character });
            }

            SendCharacterStatus(ch, includeExtendedStats: ch == _character);
        }
        else if (type == 5) // skill list
        {
            SendSkillList();
        }
    }

    // ==================== Target Response ====================
}
