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

    public void HandleSingleClick(uint uid)
    {
        if (_character == null) return;

        var obj = _world.FindObject(new Serial(uid));
        if (obj == null) return;

        // Fire @Click trigger
        if (_triggerDispatcher != null)
        {
            if (obj is Character clickCh)
            {
                var result = _triggerDispatcher.FireCharTrigger(clickCh, CharTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ScriptConsole = this });
                if (result == TriggerResult.True)
                    return;
            }
            else if (obj is Item clickItem)
            {
                var result = _triggerDispatcher.FireItemTrigger(clickItem, ItemTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ItemSrc = clickItem, ScriptConsole = this });
                if (result == TriggerResult.True)
                    return;
            }
        }

        // Overhead name: for characters, the hue follows notoriety so the
        // label reads blue/green/grey/orange/red/yellow. Items stay grey.
        ushort nameHue = 0x03B2;
        if (obj is Character labelCh)
            nameHue = NotoToHue(GetNotoriety(labelCh));
        _netState.Send(new PacketSpeechUnicodeOut(
            uid, (ushort)(obj is Character c ? c.BodyId : 0),
            6, nameHue, 3, "TRK", "", obj.GetName()));
    }

    /// <summary>Convert a notoriety byte (1-7) to the hue used for
    /// overhead labels and system speech. Values mirror Source-X
    /// CServerConfig::m_iColorNoto* defaults:
    /// good/innocent=0x59 blue, guild-same=0x3f green, neutral=0x3b2 grey,
    /// criminal=0x3b2 grey, guild-war=0x90 orange, evil/murderer=0x22 red,
    /// invul=0x35 yellow.</summary>
    private static ushort NotoToHue(byte noto) => noto switch
    {
        1 => 0x0059, // innocent / blue
        2 => 0x003F, // friend (party/guild-ally) / green
        4 => 0x03B2, // criminal / grey
        5 => 0x0090, // enemy guild / orange
        6 => 0x0022, // murderer / red
        7 => 0x0035, // invulnerable / yellow
        _ => 0x03B2, // neutral / grey (NPC default)
    };

    // ==================== Item Pick Up ====================

    public void HandleItemPickup(uint serial, ushort amount)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null)
        {
            SendPickupFailed(5); // doesn't exist
            return;
        }

        // Fire @Pickup trigger
        if (_triggerDispatcher != null)
        {
            var trigger = item.ContainedIn.IsValid ? ItemTrigger.PickupPack : ItemTrigger.PickupGround;
            var result = _triggerDispatcher.FireItemTrigger(item, trigger,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            if (result == TriggerResult.True)
            {
                SendPickupFailed(1);
                return;
            }
        }

        int dist = _character.Position.GetDistanceTo(item.Position);
        if (dist > 3 && !item.ContainedIn.IsValid && _character.PrivLevel < PrivLevel.GM)
        {
            SendPickupFailed(4); // too far away
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

        if (item.BaseId == 0x0EED)
            SendCharacterStatus(_character);
    }

    // ==================== Item Drop ====================

    public void HandleItemDrop(uint serial, short x, short y, sbyte z, uint containerUid)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        _character.RemoveTag("DRAGGING");

        if (containerUid != 0 && containerUid != 0xFFFFFFFF)
        {
            var container = _world.FindItem(new Serial(containerUid));
            if (container != null && _tradeManager?.FindByContainer(containerUid) is { } dropTrade)
            {
                if (!dropTrade.IsParticipant(_character))
                {
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
                var myCont = dropTrade.GetOwnContainer(_character);
                myCont.AddItem(item);
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
                // Capacity enforcement — bank and normal containers have separate limits.
                // Staff bypass so GMs can overstuff chests during testing.
                // On rejection we bounce the item into the dropper's backpack so it
                // is never destroyed; client visually resyncs from our 0x25/0x3C.
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    bool isBank = container.EquipLayer == Layer.BankBox;
                    int currentCount = _world.GetContainerContents(container.Uid).Count();
                    int maxItems = isBank ? _world.MaxBankItems : _world.MaxContainerItems;
                    if (currentCount >= maxItems)
                    {
                        SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullItems : Msg.ContFullItems));
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                    int weightLimit = isBank ? _world.MaxBankWeight : _world.MaxContainerWeight;
                    if (weightLimit > 0)
                    {
                        int totalWeight = 0;
                        foreach (var b in _world.GetContainerContents(container.Uid))
                            totalWeight += Math.Max(1, (int)b.Amount);
                        if (totalWeight + Math.Max(1, (int)item.Amount) > weightLimit)
                        {
                            SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullWeight : Msg.ContFullWeight));
                            PlaceItemInPack(_character, item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                }

                // Fire @DropOn_Item
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = container });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }
                container.AddItem(item);
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
                    InitiateTrade(charTarget, item);
                    _netState.Send(new PacketDropAck());
                    return;
                }

                PlaceItemInPack(charTarget, item);
                _netState.Send(new PacketDropAck());
                return;
            }
        }

        // Source-X parity: @DropOn_Ground RETURN 1 cancels the drop;
        // bounce the item back to the player's pack so the cursor
        // doesn't get stuck and scripts can fully gate ground placement.
        var dropResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.DropOnGround,
            new TriggerArgs { CharSrc = _character, ItemSrc = item });
        if (dropResult == TriggerResult.True)
        {
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropReject());
            return;
        }

        _world.PlaceItemWithDecay(item, new Point3D(x, y, z, _character.MapIndex));
        _netState.Send(new PacketDropAck());
    }

    // ==================== Item Equip ====================

    public void HandleItemEquip(uint serial, byte layer, uint charSerial)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

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

        // Spell interruption on equip change
        _spellEngine?.TryInterruptFromEquip(target);

        target.Equip(item, (Layer)layer);

        var wornPkt = new PacketWornItem(
            item.Uid.Value, item.DispIdFull, layer,
            target.Uid.Value, item.Hue);
        _netState.Send(wornPkt);
        BroadcastNearby?.Invoke(target.Position, UpdateRange, wornPkt, _character.Uid.Value);

        _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Equip,
            new TriggerArgs { CharSrc = _character, ItemSrc = item });
    }

    // ==================== Status Request ====================

    public void HandleProfileRequest(byte mode, uint serial, string bioText = "")
    {
        if (_character == null) return;

        Character? ch = _world.FindChar(new Serial(serial));
        ch ??= _character;

        if (mode == 1)
        {
            if (ch == _character || _character.PrivLevel >= PrivLevel.GM)
                ch.SetTag("PROFILE_BIO", bioText);
            return;
        }

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
