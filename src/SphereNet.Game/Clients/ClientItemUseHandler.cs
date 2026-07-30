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
/// Item-use handler extracted from the GameClient.ItemUse partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Double-click dispatch, per-type item use, pet speech commands, vendor
/// buy/sell lists. Method bodies moved verbatim; the private context shims
/// below enumerate exactly what this handler needs from GameClient.
/// </summary>
public sealed class ClientItemUseHandler
{
    private readonly IClientContext _client;

    internal ClientItemUseHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private HousingEngine? _housingEngine => _client.Housing;
    private SkillHandlers? _skillHandlers => _client.SkillH;
    private Mounts.MountEngine? _mountEngine => _client.MountE;
    private ILogger _logger => _client.Log;
    private const int UpdateRange = GameClient.UpdateRange;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint, Character>? BroadcastMoveNearby => _client.BroadcastMoveNearby;
    private Action<Character>? BroadcastCharacterAppear => _client.BroadcastCharacterAppear;
    private Action<Serial, SphereNet.Network.Packets.PacketWriter>? SendToChar => _client.SendToChar;
    private static Action<Character>? OnWakeNpc => GameClient.OnWakeNpc;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private byte GetNotoriety(Character ch) => _client.GetNotoriety(ch);
    private static byte BuildMobileFlags(Character ch) => GameClient.BuildMobileFlags(ch);
    private void PlaceItemInPack(Character target, Item item) => _client.PlaceItemInPack(target, item);
    private void SendWorldItem(Item item) => _client.SendWorldItem(item);
    private Item? GetTopContainer(Item item) => _client.GetTopContainer(item);
    private void SendOpenContainer(Item container) => _client.SendOpenContainer(container);
    private void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) => _client.SendGump(gump, callback);
    private void SendInputPromptGump(IScriptObj target, string propName, int maxLength) => _client.SendInputPromptGump(target, propName, maxLength);
    private void SendPaperdoll(Character ch) => _client.SendPaperdoll(ch);
    private void SendSelfRedraw() => _client.SendSelfRedraw();
    private void OpenCraftingGump(SkillType craftSkill) => _client.OpenCraftingGump(craftSkill);
    private void OpenGuildStoneGump(Item stone) => _client.OpenGuildStoneGump(stone);
    private void OpenHouseSignGump(Item signOrMulti) => _client.OpenHouseSignGump(signOrMulti);
    private void OpenBook(Item book, bool writable) => _client.OpenBook(book, writable);
    private void HandleCastSpell(SpellType spell, uint targetUid) => _client.HandleCastSpell(spell, targetUid);
    private void OnResurrect() => _client.OnResurrect();
    private void ObjectMessage(Objects.ObjBase target, string text) => _client.ObjectMessage(target, text);
    private void NpcSpeech(Character npc, string text) => _client.NpcSpeech(npc, text);
    private void BroadcastDeleteObject(uint uid) => _client.BroadcastDeleteObject(uid);
    private void BroadcastDrawObject(Character ch) => _client.BroadcastDrawObject(ch);
    private Character? DismountCharacter() => _client.DismountCharacter();
    private bool TryMountCharacter(Character mount) => _client.TryMountCharacter(mount);
    private void ResetWalkValidator() => _client.ResetWalkValidator();
    private void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1) => _client.SetPendingTarget(callback, cursorType);

    /// <summary>Source-X OnTarg_Use_Item: arm a target cursor for a USED item.
    /// The source item and its current parent are pinned on the target state;
    /// HandleTargetResponse re-validates both before running the callback
    /// (deleted/moved/handed-away source refuses the use) and fires the
    /// item's @TargOn_Char/@TargOn_Item/@TargOn_Ground triggers (RETURN 1
    /// cancels). Every native "use item → pick target" flow must arm through
    /// this, not the bare SetPendingTarget.</summary>
    private void SetPendingItemTarget(Item source, Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1)
    {
        _client.SetPendingTarget(callback, cursorType);
        _client.Targets.ItemUid = source.Uid;
        _client.Targets.ItemParentUid = source.ContainedIn;
    }
    private void SetPendingMultiTarget(Action<uint, short, short, sbyte, ushort> callback,
        ushort multiId, short xOff, short yOff, short zOff, ushort hue) =>
        _client.SetPendingMultiTarget(callback, multiId, xOff, yOff, zOff, hue);
    private void ToggleDoor(Item door) => _client.ToggleDoor(door);
    private bool TryToggleNearestMapStaticDoor(uint clientSerial) => _client.TryToggleNearestMapStaticDoor(clientSerial);
    private void UsePotion(Item potion) => _client.UsePotion(potion);

    /// <summary>Consume exactly one unit of a used consumable: decrement a stack and
    /// resend it, or delete the last unit (honouring an @Destroy RETURN 1). Never
    /// bulk-deletes a stack — Source-X burns one unit per use (Use_Eat/Use_Drink
    /// wConsume=1), so a single drink must not wipe the whole pile.</summary>
    private void ConsumeOneOnUse(Item item)
    {
        if (item.Amount > 1)
        {
            item.Amount--;
            if (item.ContainedIn.IsValid)
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0, item.Amount, item.X, item.Y,
                    item.ContainedIn.Value, item.Hue, _netState.IsClientPost6017));
            else
                SendWorldItem(item);
        }
        else if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                     new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
        {
            _world.RemoveItem(item);
        }
    }
    private static int GetVendorItemPrice(Character vendor, Item item) => GameClient.GetVendorItemPrice(vendor, item);
    private static int GetVendorItemSellPrice(Character vendor, Item item) => GameClient.GetVendorItemSellPrice(vendor, item);

    public void HandleDoubleClick(uint uid)
    {
        if (_character == null) return;

        // Bit 31 = paperdoll request flag (client status bar button, Alt+DClick)
        bool paperdollRequest = (uid & 0x80000000) != 0;
        uid &= 0x7FFFFFFF;

        if (paperdollRequest)
        {
            var target = uid == _character.Uid.Value
                ? _character
                : _world.FindChar(new Serial(uid));
            if (target != null && CanSeeCharacterForDoubleClick(target))
                SendPaperdoll(target);
            else if (uid != 0)
                Send(new PacketDeleteObject(uid));
            return;
        }

        if (uid == _character.Uid.Value)
        {
            // If mounted, dismount on self-dclick
            if (_character.IsMounted && _mountEngine != null)
            {
                // COMBAT_DCLICKSELF_UNMOUNTS gate: mid-fight the paperdoll
                // opens instead, so a war-mode dclick can't accidentally
                // dismount — unless the flag opts into the unmount.
                if (SphereNet.Game.Combat.CombatHelper.DClickSelfKeepsMount(_character))
                {
                    SendPaperdoll(_character);
                    return;
                }
                uint oldMountItemUid = _character.GetEquippedItem(Layer.Horse)?.Uid.Value ?? 0;
                BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                    new PacketSound(0x0140, _character.X, _character.Y, _character.Z), 0);
                var npc = DismountCharacter();

                // Re-seat after the body type change (mounted→foot) via the
                // shared standing resolver (multi decks/house floors included).
                var dismountStand = _world.Standing.ResolveStandingSurface(_character,
                    _character.MapIndex, _character.X, _character.Y, _character.Z,
                    SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
                if (dismountStand.Found && dismountStand.Z != _character.Z)
                {
                    _logger.LogInformation("[DISMOUNT] Z correction: {OldZ} -> {NewZ}", _character.Z, dismountStand.Z);
                    _character.Position = new Point3D(_character.X, _character.Y, dismountStand.Z, _character.MapIndex);
                }

                if (oldMountItemUid != 0)
                    BroadcastDeleteObject(oldMountItemUid);

                ResetWalkValidator();
                _netState.WalkSequence = 0;
                _netState.SendPriority(new PacketMoveReject(0,
                    _character.X, _character.Y, _character.Z,
                    (byte)((byte)_character.Direction & 0x07)));

                byte flags = BuildMobileFlags(_character);
                byte dir77 = (byte)((byte)_character.Direction & 0x07);
                byte noto = GetNotoriety(_character);
                var movePacket = new PacketMobileMoving(
                    _character.Uid.Value, _character.BodyId,
                    _character.X, _character.Y, _character.Z, dir77,
                    _character.Hue, flags, noto);
                _netState.Send(movePacket);
                if (BroadcastMoveNearby != null)
                    BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
                else
                    BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

                if (npc != null)
                {
                    npc.ClearStatFlag(StatFlag.Ridden);
                    BroadcastCharacterAppear?.Invoke(npc);
                }
                return;
            }
            SendPaperdoll(_character);
            return;
        }

        var item = _world.FindItem(new Serial(uid));
        if (item != null)
        {
            if (!CanSeeItemForDoubleClick(item, out Point3D usePoint))
            {
                Send(new PacketDeleteObject(uid));
                return;
            }

            FaceUsePoint(usePoint);

            if (_character.PrivLevel < PrivLevel.GM)
            {
                // Loose ground item: simple tile-distance reach. A contained item
                // must be reachable through its top parent (Source-X CClientUse:
                // a crafted/stale DClick can't reach into a far or moved
                // container). CanReachTargetItem covers both the on-ground and
                // worn-by-a-mobile top-container cases.
                bool reachable = CanReachTargetItem(item);
                if (!reachable)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
                    return;
                }
            }

            // Fire @DClick on item — if script returns true, block default action
            if (_triggerDispatcher != null)
            {
                var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DClick,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                if (result == TriggerResult.True)
                    return;
            }
            HandleItemUse(item);
            return;
        }

        var ch = _world.FindChar(new Serial(uid));
        if (ch != null)
        {
            if (!CanSeeCharacterForDoubleClick(ch))
            {
                Send(new PacketDeleteObject(uid));
                return;
            }

            FaceUsePoint(ch.Position);

            // Fire @DClick on character — if script returns true, block default action
            if (_triggerDispatcher != null)
            {
                var result = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.DClick,
                    new TriggerArgs { CharSrc = _character });
                if (result == TriggerResult.True)
                    return;
            }
            if (VendorEngine.IsVendorLike(ch))
            {
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    int dist = Math.Max(Math.Abs(_character.X - ch.X), Math.Abs(_character.Y - ch.Y));
                    if (dist > 3 || _character.MapIndex != ch.MapIndex)
                    {
                        SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
                        return;
                    }
                }
                HandleVendorInteraction(ch);
                return;
            }

            // Mount check — double-click mountable NPC
            if (!ch.IsPlayer && _mountEngine != null &&
                Mounts.MountEngine.IsMountable(ch.BodyId))
            {
                if (ch.IsDead)
                {
                    SysMessage(ServerMessages.Get(Msg.MsgBondedDeadCantmount));
                    return;
                }

                // Already riding — block with message instead of falling through to paperdoll
                if (_character.IsMounted)
                {
                    SysMessage(ServerMessages.Get("mount_already_riding"));
                    return;
                }

                // UO mount-range rule: the mount must be adjacent (within 1 tile).
                // Without this check, a distant mount gets accepted by the server
                // while the client teleports the player to the mount's tile — the
                // classic "I got yanked onto my horse" glitch.
                int dx = Math.Abs(_character.X - ch.X);
                int dy = Math.Abs(_character.Y - ch.Y);
                if (_character.MapIndex != ch.MapIndex || dx > 1 || dy > 1)
                {
                    SysMessage("That is too far away.");
                    return;
                }

                if (TryMountCharacter(ch))
                {
                    uint mountNpcUid = ch.Uid.Value;
                    BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                        new PacketSound(0x0140, _character.X, _character.Y, _character.Z), 0);

                    // Re-seat after the body type change (foot→mounted) via
                    // the shared standing resolver (multi decks included).
                    var mountedStand = _world.Standing.ResolveStandingSurface(_character,
                        _character.MapIndex, _character.X, _character.Y, _character.Z,
                        SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
                    if (mountedStand.Found && mountedStand.Z != _character.Z)
                        _character.Position = new Point3D(_character.X, _character.Y, mountedStand.Z, _character.MapIndex);

                    // Immediately remove the old NPC mount from nearby clients to prevent temporary duplicates.
                    BroadcastDeleteObject(mountNpcUid);

                    // Reset walk state — foot→mount speed transition
                    _netState.WalkSequence = 0;
                    ResetWalkValidator();

                    // MoveReject FIRST — clears walk queue + Offset.Z, sets exact position
                    _netState.SendPriority(new PacketMoveReject(0,
                        _character.X, _character.Y, _character.Z,
                        (byte)((byte)_character.Direction & 0x07)));

                    // DrawObject AFTER — body/equipment update with Steps queue already cleared.
                    // BroadcastDrawObject sends to self + nearby clients.
                    BroadcastDrawObject(_character);
                    return;
                }

                SysMessage(ServerMessages.Get("gm_mount_failed"));
                return;
            }

            // Source-X: pack horses/llamas expose their pack to players, while
            // staff can inspect the pack of every non-human NPC without snoop
            // or touch checks.
            if (!ch.IsPlayer && !IsHumanLikeBody(ch.BodyId) &&
                (ch.BodyId is 0x0123 or 0x0124 || _character.PrivLevel >= PrivLevel.GM))
            {
                SendOpenContainer(EnsureCharacterPack(ch));
                return;
            }

            if (IsHumanLikeBody(ch.BodyId))
                SendPaperdoll(ch);

            return;
        }

        // Static map doors have no world object — their dclick arrives with the
        // synthetic serial we drew them with. This MUST run only after the item
        // and character lookups both missed: it toggles the nearest door to the
        // PLAYER without validating the clicked uid, so reaching it with a live
        // character's uid toggled a random nearby door and rebroadcast the door
        // art under that character's serial (the "dclicked cow vanishes with a
        // door sound" bug).
        if (TryToggleNearestMapStaticDoor(uid))
            return;

        if (uid != 0)
            Send(new PacketDeleteObject(uid));
    }

    private bool CanSeeCharacterForDoubleClick(Character target)
    {
        if (_character == null || target.IsDeleted) return false;
        if (target == _character) return true;
        if (target.MapIndex != _character.MapIndex) return false;
        if (_character.Position.GetDistanceTo(target.Position) > Math.Max(UpdateRange, (int)_netState.ViewRange))
            return false;

        bool concealed = target.IsStatFlag(StatFlag.Hidden) || target.IsInvisible;
        if (concealed && !_character.AllShow && _character.PrivLevel < PrivLevel.Counsel)
            return false;

        // Distance + visibility only (Source-X CanSee) — no LOS raycast. The view
        // pipeline draws mobiles through walls, so failing here on a raycast made
        // the dclick path "correct" the client with PacketDeleteObject and the
        // visible mobile vanished until the next view refresh.
        return true;
    }

    private bool CanSeeItemForDoubleClick(Item item, out Point3D usePoint)
    {
        usePoint = item.Position;
        if (_character == null || item.IsDeleted) return false;

        var top = GetTopContainer(item) ?? item;
        Character? owner = top.ContainedIn.IsValid ? _world.FindChar(top.ContainedIn) : null;
        usePoint = owner?.Position ?? top.Position;

        if (owner == _character)
            return true;
        if (usePoint.Map != _character.MapIndex)
            return false;
        // Invisible items (spawn worldgems, triggers) render for AllShow AND for
        // GM+ staff without toggling AllShow (ClientViewUpdater canSeeInvisItems).
        // The can-see gate here must use the SAME audience, or a GM who legitimately
        // sees an invisible spawner double-clicks it, the server decides it is
        // unseeable, and "corrects" the client with PacketDeleteObject — the item
        // vanishes from view (still alive server-side) instead of the double-click
        // triggering the spawner.
        if (item.IsAttr(ObjAttributes.Invis) && !_character.AllShow &&
            _character.PrivLevel < PrivLevel.GM)
            return false;
        if (owner != null && !CanSeeCharacterForDoubleClick(owner))
            return false;
        if (_character.Position.GetDistanceTo(usePoint) > Math.Max(UpdateRange, (int)_netState.ViewRange))
            return false;

        // Distance + visibility only (Source-X Event_DoubleClick → CanSee) —
        // no LOS raycast, same rule as the mobile path above. The view
        // pipeline draws items through walls, so failing here on a raycast
        // "corrected" the client with PacketDeleteObject and the item
        // vanished: with the house walls now real LOS occluders, the house
        // sign disappeared when double-clicked from INSIDE the house. Reach
        // is enforced separately per use (the non-GM touch checks below).
        return true;
    }

    private void FaceUsePoint(Point3D point)
    {
        if (_character == null ||
            (GameClient.ServerOptionFlags & OptionFlags.NoDClickTurn) != 0 ||
            point.Map != _character.MapIndex ||
            (point.X == _character.X && point.Y == _character.Y))
            return;

        Direction direction = _character.Position.GetDirectionTo(point);
        if (direction == _character.Direction) return;

        // Field diagnostic for the "dclick in a dungeon bumps me up" report:
        // this redraw re-imposes the stored server Z, so a bump here means the
        // stored Z had ALREADY drifted from the true standing Z at this tile —
        // log the pair to locate the drift source. The comparison runs through
        // the shared standing resolver: the old GetEffectiveZ check could not
        // see multis/dynamics and produced false alarms on ship decks and
        // house floors. Log-only — the audit do-not list forbids correcting
        // Z here (it would mask the real drift source).
        if (_world.MapData != null)
        {
            var driftStand = _world.Standing.ResolveStandingSurface(_character,
                _character.MapIndex, _character.X, _character.Y, _character.Z,
                SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
            if (driftStand.Found && driftStand.Z != _character.Z)
                _logger.LogInformation(
                    "[z_drift] dclick-facing at {X},{Y} map {Map}: stored Z={Stored}, standing Z={Stand}",
                    _character.X, _character.Y, _character.MapIndex, _character.Z, driftStand.Z);
        }

        _character.Direction = direction;
        SendSelfRedraw();

        byte flags = BuildMobileFlags(_character);
        byte noto = GetNotoriety(_character);
        var moving = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z,
            (byte)((byte)direction & 0x07), _character.Hue, flags, noto);
        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby(_character.Position, UpdateRange, moving, _character.Uid.Value, _character);
        else
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, moving, _character.Uid.Value);
    }

    private Item EnsureCharacterPack(Character target)
    {
        var pack = target.Backpack ?? target.GetEquippedItem(Layer.Pack);
        if (pack != null) return pack;

        pack = _world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        pack.Name = "backpack";
        target.Equip(pack, Layer.Pack);
        target.Backpack = pack;
        return pack;
    }

    private static bool IsHumanLikeBody(ushort body) =>
        body is 0x0190 or 0x0191 or 0x0192 or 0x0193
            or 0x025D or 0x025E or 0x029A or 0x029B;

    /// <summary>
    /// Source-X CClient::Cmd_Use_Item parity dispatcher.
    /// The Source-X switch handles ~30 IT_* branches; SphereNet mirrors each
    /// branch to either a real handler or, when the underlying engine is not
    /// yet ported, the matching DEFMSG_ITEMUSE_* + target-cursor prompt so
    /// players see the exact upstream UX. Anything not matched falls through
    /// to DEFMSG_ITEMUSE_CANTTHINK like upstream.
    /// </summary>
    /// <summary>Convert a bank check item into gold in the container it occupies
    /// (bank box or backpack) and consume the check. Source-X check redeem flow:
    /// new gold pile(s) sent via 0x25, the check removed via 0x1D.</summary>
    private void RedeemBankCheck(Item check, int amount)
    {
        if (_character == null) return;
        var container = check.ContainedIn.IsValid ? _world.FindItem(check.ContainedIn) : null;
        container ??= _character.Backpack;
        if (container == null) return;

        int pileCount = amount > 0 ? (int)(((long)amount + 59_999) / 60_000) : 0;
        if (amount <= 0 || container.ContentCount + pileCount > Item.MaxContainerItems)
        {
            SysMessage("That container cannot hold the redeemed gold.");
            return;
        }

        int remaining = amount;
        while (remaining > 0)
        {
            int pile = Math.Min(remaining, 60000);
            var gold = _world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.ItemType = ItemType.Gold;
            gold.Amount = (ushort)pile;
            gold.Name = "Gold";
            var actual = container.AddItemWithStack(gold);
            if (actual != gold) _world.RemoveItem(gold);
            _netState.Send(new PacketContainerItem(
                actual.Uid.Value, actual.DispIdFull, 0, actual.Amount,
                actual.X, actual.Y, container.Uid.Value, actual.Hue,
                _netState.IsClientPost6017));
            remaining -= pile;
        }

        _netState.Send(new PacketDeleteObject(check.Uid.Value));
        _world.RemoveItem(check);
    }

    private void HandleItemUse(Item item)
    {
        if (_character == null) return;
        if (_character.IsDead)
        {
            if (item.ItemType == ItemType.Shrine)
            {
                OnResurrect();
                SysMessage(ServerMessages.GetFormatted(Msg.HealingRes, _character.Name));
                return;
            }
            SysMessage(ServerMessages.Get("death_cant_while_dead"));
            return;
        }
        // Frozen chars can't use items — EXCEPT struggling against the web
        // that holds them (Source-X Use_Item_Web runs while stuck).
        if (_character.IsStatFlag(StatFlag.Freeze) && item.ItemType != ItemType.Web)
        {
            SysMessage(ServerMessages.Get("msg_frozen"));
            return;
        }

        // Bank check redeem (Source-X): double-clicking a check converts it to gold
        // in the container it sits in (bank box or backpack) and consumes the check.
        if (item.TryGetTag("BANKCHECK_AMOUNT", out string? checkStr) &&
            int.TryParse(checkStr, out int checkAmount) && checkAmount > 0)
        {
            RedeemBankCheck(item, checkAmount);
            return;
        }

        // Source-X CClient::Cmd_Use_Item: an equippable item that is not
        // currently equipped is armed/worn first on double-click — this is how
        // a weapon or tool lying on the ground (or in the pack) reaches the
        // hand — and the use-type behavior below then runs from the hand.
        // Spellbooks and ground light sources keep their open/toggle behavior.
        if (!item.IsEquipped &&
            item.ItemType != ItemType.Spellbook &&
            !((item.ItemType is ItemType.LightLit or ItemType.LightOut) && !item.ContainedIn.IsValid))
        {
            Layer wearLayer = ResolveWearableLayer(item);
            if (wearLayer is not Layer.None and not Layer.Pack and not Layer.Hair and not Layer.FacialHair &&
                (int)wearLayer < (int)Layer.Horse)
                _client.TryDClickEquip(item, wearLayer);
        }

        // Source-X Cmd_Use_Item detaches a spawned ground item before running
        // its use-type behavior. It can then be replaced even if using it does
        // not consume or delete the object.
        DetachFromItemSpawner(item);

        switch (item.ItemType)
        {
            // ---- containers / corpses ----
            case ItemType.Container:
            case ItemType.TrashCan:
            case ItemType.ShipHold:
            {
                // Snoop gate: opening another player's sub-container requires Snooping skill
                if (_character.PrivLevel < PrivLevel.GM && item.ItemType == ItemType.Container)
                {
                    var containerOwner = ResolveContainerOwner(item);
                    if (containerOwner != null && containerOwner != _character && containerOwner.IsPlayer)
                    {
                        bool snoopOk = Skills.Information.ActiveSkillEngine.Snooping(
                            new GameClient.InfoSkillSink(_client, _character), item);
                        if (!snoopOk)
                            break;
                    }
                }
                // Trapped container: fire trap on open, then disarm
                if (item.TryGetTag("TRAP_DAMAGE", out string? trapDmgStr) &&
                    int.TryParse(trapDmgStr, out int trapDmg) && trapDmg > 0)
                {
                    if (!CombatEngine.IsDamageImmune(_character))
                    {
                        _character.Hits -= (short)Math.Min(trapDmg, _character.Hits);
                        SysMessage("You set off a trap!");
                    }
                    item.RemoveTag("TRAP_DAMAGE");
                }
                SendOpenContainer(item);
                break;
            }
            case ItemType.Corpse:
                SendOpenContainer(item);
                break;

            case ItemType.ContainerLocked:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                if (FindBackpackKeyFor(item) != null)
                    SysMessage(ServerMessages.Get(Msg.LockHasKey));
                else
                    SysMessage(ServerMessages.Get(Msg.LockContNoKey));
                break;

            case ItemType.ShipHoldLock:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                if (FindBackpackKeyFor(item) != null)
                    SysMessage(ServerMessages.Get(Msg.LockHasKey));
                else
                    SysMessage(ServerMessages.Get(Msg.LockHoldNoKey));
                break;

            // ---- ship plank / side (Source-X CItem::Ship_Plank) ----
            case ItemType.ShipSide:
                // Open the plank; it autocloses after 5 seconds.
                item.OpenPlank();
                break;
            case ItemType.ShipSideLocked:
                if (FindBackpackKeyFor(item) != null)
                {
                    item.OpenPlank();
                }
                else
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                    SysMessage(ServerMessages.Get(Msg.LockContNoKey));
                }
                break;
            case ItemType.ShipPlank:
                // An open plank: dclick from off the plank steps you aboard
                // (Source-X teleports the user to the plank); standing on it,
                // dclick swings it shut.
                if (_character.Position.X != item.X || _character.Position.Y != item.Y)
                {
                    var shipEngine = Item.ResolveShipEngine?.Invoke();
                    var ship = shipEngine?.GetShip(item.Link) ?? shipEngine?.FindShipAt(item.Position);
                    if (ship != null && _character.PrivLevel < PrivLevel.GM && !ship.CanBoard(_character.Uid))
                    {
                        SysMessage(ServerMessages.Get(Msg.TillerNotyourship));
                        break;
                    }
                    _world.MoveCharacter(_character,
                        new Point3D(item.X, item.Y, (sbyte)(item.Z + 3), item.MapIndex));
                    SendSelfRedraw();
                }
                else
                {
                    item.ClosePlank();
                }
                break;

            // ---- doors ----
            case ItemType.Door:
            case ItemType.DoorOpen:
                ToggleDoor(item);
                break;
            case ItemType.DoorLocked:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                break;

            case ItemType.Trap:
            case ItemType.TrapActive:
            {
                // Source-X CCharUse Do_Use_Item IT_TRAP: using a trap SPRINGS it
                // (arms the graphic, damages the user when in touch range) — it
                // does not open the RemoveTrap skill; disarming goes through the
                // skill list target flow.
                int trapDmg = item.UseTrap();
                // Source-X gates the damage on CanTouch — the shard-wide reach
                // distance (3 tiles, same as the use-reach gate above).
                if (_character.Position.GetDistanceTo(item.Position) <= 3 &&
                    !CombatEngine.IsDamageImmune(_character))
                {
                    _character.Hits -= (short)Math.Min(trapDmg, _character.Hits);
                    SysMessage("You set off a trap!");
                    if (_character.Hits <= 0 && !_character.IsDead)
                    {
                        if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(_character, null);
                        else _character.Kill();
                    }
                }
                break;
            }

            // ---- consumables / potions / books ----
            case ItemType.Potion:
                UsePotion(item);
                break;
            case ItemType.Food:
            case ItemType.Fruit:
            case ItemType.Drink:
                // Source-X Use_Eat/Use_Drink refuse an item the user cannot move
                // (CCharUse.cpp:927/992 CanMoveItem gate) BEFORE consuming it — so a
                // placed Move_Never/locked food fixture is never destroyed by a
                // non-GM double-click. GM and movable pack items pass.
                if (!ItemMoveRules.CanMove(_character, item, out _))
                {
                    SysMessage(ServerMessages.Get(
                        item.ItemType == ItemType.Drink ? Msg.DrinkCantmove : Msg.FoodCantmove));
                    break;
                }
                // @Eat (Source-X) — RETURN 1 blocks the meal. N1 = hunger restored.
                if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Eat,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item, N1 = 5 }) == TriggerResult.True)
                    break;
                _character.Food = (ushort)Math.Min(_character.Food + 5, 60);
                SysMessage(ServerMessages.Get("itemuse_eat_food"));
                BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                    new PacketAnimation(_character.Uid.Value, (ushort)AnimationType.Eat), 0);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                    new PacketSound(0x003A, _character.X, _character.Y, _character.Z), 0);
                if (item.Amount > 1)
                {
                    // Eat a single unit, don't wipe the whole stack.
                    item.Amount--;
                    if (item.ContainedIn.IsValid)
                        _netState.Send(new PacketContainerItem(
                            item.Uid.Value, item.DispIdFull, 0, item.Amount, item.X, item.Y,
                            item.ContainedIn.Value, item.Hue, _netState.IsClientPost6017));
                    else
                        SendWorldItem(item);
                }
                else if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
                {
                    _world.RemoveItem(item);
                }
                break;

            // Source-X routes t_grain/t_grass through Use_Eat and t_water_wash
            // through Use_Drink (CCharUse.cpp). These are almost always fixed
            // sources — water troughs/basins, decorative grass and hay — so the
            // hunger benefit is given but the item is NEVER deleted; only a
            // movable stack sitting in a container is decremented a unit. This
            // keeps a placed trough/tile intact (it was previously a dead
            // "you can't think of a way to use that" default).
            case ItemType.Grain:
            case ItemType.Grass:
            case ItemType.WaterWash:
                if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Eat,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item, N1 = 5 }) == TriggerResult.True)
                    break;
                _character.Food = (ushort)Math.Min(_character.Food + 5, 60);
                SysMessage(ServerMessages.Get("itemuse_eat_food"));
                BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                    new PacketAnimation(_character.Uid.Value, (ushort)AnimationType.Eat), 0);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                    new PacketSound(0x003A, _character.X, _character.Y, _character.Z), 0);
                if (item.ContainedIn.IsValid && item.Amount > 1)
                {
                    item.Amount--;
                    _netState.Send(new PacketContainerItem(
                        item.Uid.Value, item.DispIdFull, 0, item.Amount, item.X, item.Y,
                        item.ContainedIn.Value, item.Hue, _netState.IsClientPost6017));
                }
                break;

            case ItemType.Book:
            case ItemType.Message:
                OpenBook(item, item.ItemType == ItemType.Book);
                break;

            case ItemType.Spellbook:
            case ItemType.SpellbookNecro:
            case ItemType.SpellbookPala:
            case ItemType.SpellbookBushido:
            case ItemType.SpellbookNinjitsu:
            case ItemType.SpellbookArcanist:
            case ItemType.SpellbookMystic:
            case ItemType.SpellbookMastery:
            case ItemType.SpellbookExtra:
            {
                // @SpellBook (Source-X) — opening a spellbook. RETURN 1 keeps it shut.
                if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellBook,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item }) == TriggerResult.True)
                    break;
                ushort scrollOffset = item.ItemType switch
                {
                    ItemType.SpellbookNecro => 101,
                    ItemType.SpellbookPala => 201,
                    ItemType.SpellbookBushido => 401,
                    ItemType.SpellbookNinjitsu => 501,
                    ItemType.SpellbookArcanist => 601,
                    ItemType.SpellbookMystic => 677,
                    ItemType.SpellbookMastery => 701,
                    _ => 1
                };
                ulong spellBits = ((ulong)item.More2 << 32) | item.More1;
                _netState.Send(new PacketSpellbookContent(
                    item.Uid.Value, item.BaseId, scrollOffset, spellBits));
                _netState.Send(new PacketOpenContainer(item.Uid.Value, 0x003E, _netState.IsClientPost7090));
                break;
            }

            // ---- tools that target a follow-up object ----
            case ItemType.Bandage:
                SysMessage(ServerMessages.Get(Msg.ItemuseBandagePromt));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Healing, new Serial(serial)));
                break;

            case ItemType.Lockpick:
                SysMessage(ServerMessages.Get("target_promt"));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Lockpicking, new Serial(serial)));
                break;

            case ItemType.Scissors:
                SysMessage(ServerMessages.Get("target_promt"));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => HandleScissorsTarget(item, new Serial(serial)));
                break;

            case ItemType.Tracker:
                SysMessage(ServerMessages.Get(Msg.ItemuseTrackerAttune));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => item.SetTag("LINK", serial.ToString()));
                break;

            case ItemType.Key:
            case ItemType.Keyring:
                if (item.ContainedIn != _character.Backpack?.Uid && _character.PrivLevel < PrivLevel.GM)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseKeyFail));
                    break;
                }
                SysMessage(ServerMessages.Get(Msg.ItemuseKeyPromt));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => HandleKeyUse(item, new Serial(serial)));
                break;

            case ItemType.HairDye:
                if (_character.GetEquippedItem(Layer.Hair) == null && _character.GetEquippedItem(Layer.FacialHair) == null)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseDyeNohair));
                    break;
                }
                ApplyHairDye(item);
                break;

            case ItemType.Dye:
                SysMessage(ServerMessages.Get(Msg.ItemuseDyeVat));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => HandleDyePickup(item, new Serial(serial)));
                break;

            case ItemType.DyeVat:
                SysMessage(ServerMessages.Get(Msg.ItemuseDyeTarg));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => HandleDyeApply(item, new Serial(serial)));
                break;

            // ---- weapons (target prompt for stab/pluck) ----
            case ItemType.WeaponSword:
            case ItemType.WeaponFence:
            case ItemType.WeaponAxe:
            case ItemType.WeaponMaceSharp:
            case ItemType.WeaponMaceStaff:
            case ItemType.WeaponMaceSmith:
                SysMessage(ServerMessages.Get(Msg.ItemuseWeaponPromt));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) =>
                {
                    var targetSerial = new Serial(serial);
                    var targetObj = targetSerial.IsValid ? _world.FindObject(targetSerial) : null;

                    // Source-X OnTarg_Use_Item sharp-weapon block: the classic
                    // blade uses beyond poisoning/repair.
                    if (targetObj is Item corpse && corpse.ItemType == ItemType.Corpse)
                    {
                        CarveCorpseWithBlade(corpse);
                        return;
                    }
                    if (targetObj is Character sheep && sheep.BodyId == 0x00CF)
                    {
                        ShearSheep(sheep);
                        return;
                    }
                    if (targetObj is Item fishItem && fishItem.ItemType == ItemType.Fish)
                    {
                        FilletFish(fishItem);
                        return;
                    }
                    if (targetObj is Item cropItem &&
                        cropItem.ItemType is ItemType.Crops or ItemType.Foliage)
                    {
                        HarvestPlant(cropItem);
                        return;
                    }
                    // Axe at a tree/ground: start Lumberjacking at the spot.
                    if (item.ItemType == ItemType.WeaponAxe && targetObj == null)
                    {
                        RouteSkillTarget(SkillType.Lumberjacking, targetSerial,
                            new Point3D(x, y, z, _character.MapIndex));
                        return;
                    }
                    if (targetObj is Item targetItem && IsWeaponItemType(targetItem.ItemType))
                    {
                        RouteSkillTarget(SkillType.Poisoning, targetSerial);
                        return;
                    }
                    if (targetObj is Item repairItem && _character.GetSkill(SkillType.Tinkering) > 0)
                    {
                        var sink = new GameClient.InfoSkillSink(_client, _character);
                        Skills.Information.ActiveSkillEngine.RepairItem(sink, repairItem);
                    }
                });
                break;

            case ItemType.WeaponMaceCrook:
                SysMessage(ServerMessages.Get(Msg.ItemuseCrookPromt));
                SetPendingTarget((serial, x, y, z, gfx) =>
                {
                    var animalUid = new Serial(serial);
                    if (_world.FindChar(animalUid) == null) return;
                    SysMessage("Where do you wish the animal to go?");
                    SetPendingTarget((destSerial, dx, dy, dz, destGfx) =>
                        RouteSkillTarget(SkillType.Herding, animalUid,
                            new Point3D(dx, dy, dz, _character.MapIndex)));
                });
                break;

            case ItemType.WeaponMacePick:
                SysMessage(ServerMessages.GetFormatted(Msg.ItemuseMacepickTarg, item.Name ?? "pick"));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Mining, new Serial(serial), new Point3D(x, y, z, _character.MapIndex)));
                break;

            // ---- pole/sextant/spyglass ----
            case ItemType.FishPole:
                SysMessage(ServerMessages.Get("fishing_promt"));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Fishing, new Serial(serial), new Point3D(x, y, z, _character.MapIndex)));
                break;
            case ItemType.Fish:
                SysMessage(ServerMessages.Get(Msg.ItemuseFishFail));
                break;
            case ItemType.Telescope:
                SysMessage(ServerMessages.Get(Msg.ItemuseTelescope));
                break;
            case ItemType.Sextant:
                // Source-X Use_Sextant: real UO sextant coordinates in degrees
                // and minutes N/S–E/W relative to the world center (Lord
                // British's throne 1323,1624; map plane 5120x4096), not raw
                // map integers.
                SysMessage(FormatSextant(_character.Position));
                break;

            // ---- spider web (Source-X Use_Item_Web) ----
            // Struggling damages the web with the char's STR; a destroyed web
            // leaves spider silk and frees anyone stuck on its tile.
            case ItemType.Web:
            {
                if (item.HitsCur <= 0)
                    item.HitsCur = 60 + Random.Shared.Next(250); // Source-X CCharUse.cpp:638 web strength
                item.HitsCur -= Math.Max(1, (int)_character.Str);
                if (item.HitsCur <= 0)
                {
                    var webPos = item.Position;
                    _world.RemoveItem(item);
                    var silk = _world.CreateItem();
                    silk.BaseId = 0x0DF8; // spider silk
                    silk.Amount = 1;
                    _world.PlaceItemWithDecay(silk, webPos);
                    if (_character.IsStatFlag(StatFlag.Freeze))
                    {
                        _character.ClearStatFlag(StatFlag.Freeze);
                        // Source-X CCharAct.cpp:466 drops the paralyze icon when
                        // the stuck layer goes away.
                        Character.OnClientBuffChanged?.Invoke(
                            _character, BuffIcon.Paralyze, false, 0, null);
                    }
                    SysMessage("You destroy the web.");
                }
                else
                {
                    SysMessage("You struggle against the web.");
                }
                break;
            }

            // ---- item stone (Source-X IT_ITEM_STONE dispenser) ----
            // MORE1 = the item id given, MORE2 = charges (0 = infinite,
            // 0xFFFF = exhausted/"dead"), MOREX = regen seconds between uses.
            case ItemType.ItemStone:
            {
                if (item.More2 == ushort.MaxValue)
                {
                    SysMessage("It is dead.");
                    break;
                }
                int regenSec = item.MoreP.X;
                if (regenSec > 0)
                {
                    long now2 = Environment.TickCount64;
                    if (item.Timeout > now2)
                    {
                        SysMessage($"The stone has not recharged yet ({(item.Timeout - now2) / 1000}s).");
                        break;
                    }
                    item.SetTimeout(now2 + regenSec * 1000L);
                }
                if (item.More1 == 0) break;

                var given = _world.CreateItem();
                given.BaseId = (ushort)item.More1;
                given.Amount = 1;
                Item? delivered = null;
                if (_character.Backpack != null &&
                    (_character.PrivLevel >= PrivLevel.GM || _character.CanCarry(given)))
                    delivered = _character.Backpack.TryAddItemWithStack(given);
                if (delivered == null)
                    _world.PlaceItemWithDecay(given, _character.Position);
                else if (delivered != given)
                    _world.RemoveItem(given);

                if (item.More2 != 0)
                {
                    item.More2 -= 1;
                    if (item.More2 == 0)
                        item.More2 = ushort.MaxValue; // exhausted
                }
                break;
            }
            case ItemType.SpyGlass:
                SysMessage(ServerMessages.Get(Msg.ItemuseTelescope));
                break;
            case ItemType.Map:
            case ItemType.MapBlank:
                OpenMapGump(item);
                break;

            // ---- ore / forge / ingot (overridable via @DClick trigger) ----
            case ItemType.Ore:
                SysMessage(ServerMessages.Get(Msg.ItemuseForge));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => HandleSmeltTarget(item, new Serial(serial)));
                break;
            case ItemType.Forge:
            case ItemType.Ingot:
                OpenCraftingGump(SkillType.Blacksmithing);
                break;

            // ---- crafting tools → default crafting gump (overridable via @DClick trigger) ----
            case ItemType.Mortar:
                OpenCraftingGump(SkillType.Alchemy);
                break;
            case ItemType.Carpentry:
            case ItemType.CarpentryChop:
                OpenCraftingGump(SkillType.Carpentry);
                break;
            case ItemType.CartographyTool:
                OpenCraftingGump(SkillType.Cartography);
                break;
            case ItemType.CookingTool:
                OpenCraftingGump(SkillType.Cooking);
                break;
            case ItemType.TinkerTools:
                OpenCraftingGump(SkillType.Tinkering);
                break;
            case ItemType.SewingKit:
                OpenCraftingGump(SkillType.Tailoring);
                break;
            case ItemType.ScrollBlank:
                OpenCraftingGump(SkillType.Inscription);
                break;

            // ---- ship / sign / shrine / runes ----
            case ItemType.ShipTiller:
            {
                // Classic dry-dock: the OWNER (or staff) double-clicking the
                // tillerman while NOT aboard converts the ship back to a deed.
                // Source-X pre-HS tiller dclick only talks and leaves redeed
                // to shard scripts (CClientUse IT_SHIP_TILLER); this pack
                // scripts none, so the engine provides the classic flow.
                // Aboard (or non-owner), the tillerman just talks.
                var tillerEngine = Item.ResolveShipEngine?.Invoke();
                var tillerShip = tillerEngine?.GetShip(item.Link)
                                 ?? tillerEngine?.FindShipAt(item.Position);
                if (tillerShip != null &&
                    tillerEngine!.FindShipAt(_character.Position) != tillerShip)
                {
                    bool tillerOwner = tillerShip.Owner == _character.Uid ||
                                       _character.PrivLevel >= PrivLevel.GM;
                    if (!tillerOwner)
                    {
                        ObjectMessage(item, ServerMessages.Get(Msg.TillerNotyourship));
                        break;
                    }
                    if (tillerEngine.RemoveShip(tillerShip.MultiItem.Uid, _character) != null)
                    {
                        SysMessage("You dry dock the ship.");
                        break;
                    }
                }
                ObjectMessage(item, ServerMessages.Get(Msg.ItemuseTillerman));
                break;
            }
            case ItemType.Shrine:
                if (_character.IsDead)
                {
                    OnResurrect();
                    SysMessage(ServerMessages.GetFormatted(Msg.HealingRes, _character.Name));
                }
                else
                    SysMessage(ServerMessages.Get("itemuse_shrine"));
                break;
            case ItemType.Rune:
            {
                // Source-X: dclicking a marked rune tells where it leads (name
                // + sextant coordinates); a blank rune just gives the name hint.
                var mark = item.MoreP;
                if (mark.X > 0 || mark.Y > 0)
                    SysMessage($"{(string.IsNullOrEmpty(item.Name) ? "a recall rune" : item.Name)}: {FormatSextant(mark)}");
                else
                    SysMessage(ServerMessages.Get(Msg.ItemuseRuneName));
                break;
            }

            // ---- bulletin / game / clock / spawn / animations ----
            case ItemType.BBoard:
            {
                // Source-X CClient::addBulletinBoard: board name (0x71 sub 0)
                // plus the message items as container contents — the client
                // then requests each header itself.
                _netState.Send(new PacketBulletinBoardOut(item.Uid.Value,
                    string.IsNullOrEmpty(item.Name) ? "bulletin board" : item.Name));
                foreach (var msg in item.Contents)
                {
                    _netState.Send(new PacketContainerItem(
                        msg.Uid.Value, msg.DispIdFull, 0, 1, 0, 0,
                        item.Uid.Value, msg.Hue, _netState.IsClientPost6017));
                }
                break;
            }
            case ItemType.GameBoard:
                if (item.ContainedIn.IsValid)
                    SysMessage(ServerMessages.Get(Msg.ItemuseGameboardFail));
                else
                    SendOpenContainer(item);
                break;
            case ItemType.Clock:
                ObjectMessage(item, FormatLocalGameTime());
                break;
            case ItemType.AnimActive:
                SysMessage(ServerMessages.Get("item_in_use"));
                break;
            case ItemType.SpawnItem:
            case ItemType.SpawnChar:
            case ItemType.SpawnChampion:
                if (item.SpawnChar != null)
                {
                    if (item.SpawnChar.HasAliveSpawns())
                    {
                        item.SpawnChar.KillAll();
                        item.SpawnChar.ResetTimer();
                        SysMessage(ServerMessages.Get(Msg.ItemuseSpawnNeg));
                    }
                    else
                    {
                        item.SpawnChar.ForceSpawn();
                        item.SpawnChar.OnTick(Environment.TickCount64);
                        SysMessage(ServerMessages.Get(Msg.ItemuseSpawnReset));
                    }
                }
                else if (item.SpawnItem != null)
                {
                    if (item.SpawnItem.CurrentCount > 0)
                    {
                        item.SpawnItem.KillAll();
                        item.SpawnItem.ResetTimer();
                        SysMessage(ServerMessages.Get(Msg.ItemuseSpawnNeg));
                    }
                    else
                    {
                        item.SpawnItem.ForceSpawn();
                        item.SpawnItem.OnTick(Environment.TickCount64);
                        SysMessage(ServerMessages.Get(Msg.ItemuseSpawnReset));
                    }
                }
                else
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseSpawnReset));
                }
                break;

            // ---- spell tools (Source-X routes via CClient::Cmd_Skill_Magery) ----
            case ItemType.Wand:
                if (item.More1 > 0)
                {
                    // Tag the source wand so the engine deducts a charge only when the
                    // cast actually succeeds (SpellEngine.CastDone) — deducting here,
                    // before the cast resolves, lost the charge on any fizzle /
                    // interrupt / target-cancel.
                    _character.SetTag("WAND_UID", item.Uid.Value.ToString());
                    HandleCastSpell((SpellType)item.More1, 0);
                }
                else
                    SysMessage("This wand has no charges.");
                break;
            case ItemType.Scroll:
                if (item.More1 > 0)
                {
                    var scrollSpell = (SpellType)item.More1;
                    _character.SetTag("SCROLL_UID", item.Uid.Value.ToString());
                    HandleCastSpell(scrollSpell, 0);
                }
                else
                {
                    SysMessage("The scroll is blank.");
                }
                break;

            // ---- crystal ball / cannon ----
            case ItemType.CrystalBall:
                break; // Source-X: gaze, no message.
            case ItemType.CannonBall:
                // Source-X IT_CANNON_BALL (CClientUse): target the muzzle to
                // feed this ball into it.
                SysMessage(ServerMessages.GetFormatted(Msg.ItemuseCballPromt, item.Name ?? "cannon ball"));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) =>
                {
                    var muzzle = _world.FindItem(new Serial(serial));
                    if (muzzle == null || muzzle.ItemType != ItemType.CannonMuzzle)
                    {
                        SysMessage(ServerMessages.Get(Msg.ItemuseCannonEmpty));
                        return;
                    }
                    FeedCannon(muzzle, item);
                });
                break;
            case ItemType.CannonMuzzle:
            {
                // Source-X IT_CANNON_MUZZLE load/fire state machine
                // (m_itCannon.m_Load, kept in MORE1): bit 1 = powder loaded,
                // bit 2 = shot loaded; fully loaded -> target and fire.
                if ((item.More1 & 1) == 0)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseCannonPowder));
                    SetPendingItemTarget(item, (serial, x, y, z, gfx) =>
                        FeedCannon(item, _world.FindItem(new Serial(serial))));
                    break;
                }
                if ((item.More1 & 2) == 0)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseCannonShot));
                    SetPendingItemTarget(item, (serial, x, y, z, gfx) =>
                        FeedCannon(item, _world.FindItem(new Serial(serial))));
                    break;
                }
                SysMessage(ServerMessages.Get(Msg.ItemuseCannonTarg));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) =>
                    FireCannon(item, new Serial(serial), x, y, z));
                break;
            }

            // ---- containers / signs / multi (existing engines) ----
            case ItemType.StoneGuild:
            case ItemType.StoneTown:
                // Source-X: IT_STONE_TOWN runs the SAME CItemStone engine as
                // guild stones (citizenship = membership, mayor = master);
                // the gump adapts its wording and memory type by stone type.
                OpenGuildStoneGump(item);
                break;
            case ItemType.Multi:
            case ItemType.MultiCustom:
            case ItemType.SignGump:
                OpenHouseSignGump(item);
                break;

            case ItemType.Deed:
                if (_housingEngine != null)
                {
                    var deedItem = item;
                    var shipEngine = Item.ResolveShipEngine?.Invoke();
                    if (!TryResolveDeedMulti(deedItem, out ushort multiId, out bool isShip))
                    {
                        // Legacy repair: deeds created on older builds carry no
                        // multi reference at all (their @Create MORE=<multidef>
                        // left neither More1 nor a tag — field-confirmed via the
                        // [deed] log). A deed's @Create body only assigns MORE,
                        // and FireCreateTrigger is instance-guarded and resets on
                        // world load, so re-running it once on a loaded legacy
                        // deed restores the reference in place. Fresh deeds have
                        // already fired it this session, so this is a no-op there.
                        deedItem.FireCreateTrigger();
                        if (!TryResolveDeedMulti(deedItem, out multiId, out isShip))
                        {
                            // Genuinely blank (base i_deed / i_deed_ship with no
                            // MORE) — say so instead of the misleading "Cannot
                            // place house here", and log what the deed carried.
                            _logger.LogWarning(
                                "[deed] unresolvable multi on 0x{Uid:X8} '{Name}' base=0x{Base:X} more1=0x{More1:X} tagMORE={More} tagM1D={M1D}",
                                deedItem.Uid.Value, deedItem.Name, deedItem.BaseId, deedItem.More1,
                                deedItem.TryGetTag("MORE", out string? tm) ? tm : "-",
                                deedItem.TryGetTag("MORE1_DEFNAME", out string? tmd) ? tmd : "-");
                            SysMessage("That deed is blank - it does not reference any structure.");
                            break;
                        }
                    }
                    if (isShip && shipEngine == null)
                    {
                        SysMessage(ServerMessages.Get("house_cant_place"));
                        break;
                    }
                    // A customizable foundation is either flagged on the deed
                    // (TAG.CUSTOMHOUSE) or determined from the resolved MULTIDEF type
                    // (t_multi_custom) — the standard first foundation deed has no tag (B13).
                    bool customFoundation = !isShip &&
                        ((deedItem.TryGetTag("CUSTOMHOUSE", out string? customTag) && customTag != "0")
                         || _housingEngine.IsCustomFoundation(multiId));
                    // Source-X deed placement asks for a target tile rather than
                    // dropping the house at the player's feet. The house anchor lands
                    // on the chosen point (the multi def offsets extend the footprint).
                    SysMessage(isShip
                        ? "Where would you like to place the ship?"
                        : "Where would you like to place the house?");

                    // B9 (Source-X OnTarg_Use_Item anti-cheat): remember where the
                    // deed lived when the cursor was raised — a deed traded/moved
                    // mid-cursor must not still place ("targ moved").
                    var deedParentAtPrompt = deedItem.ContainedIn;
                    // B8: the multi footprint drives both the 0x99 preview offsets
                    // and the Source-X anchor-Y correction on the reply
                    // (CItemMulti.cpp:3288 pt.m_y -= rect.bottom - 1).
                    var multiDef = _housingEngine.MultiDefs.Get(multiId);
                    short anchorBottom = multiDef?.MaxY ?? 0;

                    Action<uint, short, short, sbyte, ushort> placeCallback = (serial, tx, ty, tz, gfx) =>
                    {
                        // B9: re-validate everything when the cursor reply arrives —
                        // the world moved on while the cursor was up (Source-X
                        // OnTarg_Use_Item + CanUse chain).
                        if (deedItem.IsDeleted || _character == null) return;
                        if (deedItem.ContainedIn != deedParentAtPrompt)
                        {
                            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
                            return;
                        }
                        if (_character.IsDead || _character.IsStatFlag(StatFlag.Freeze))
                            return;
                        if (_character.PrivLevel < PrivLevel.GM && !CanReachTargetItem(deedItem))
                        {
                            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
                            return;
                        }

                        // B8: undo the client-side preview offset baked into the
                        // reply Y for real multis (Source-X Multi_Create).
                        short anchorY = ty;
                        if (anchorBottom > 0)
                            anchorY = (short)(ty - (anchorBottom - 1));
                        var pos = new Point3D(tx, anchorY, tz, _character.MapIndex);
                        Item? placedMulti;
                        SphereNet.Game.Housing.PlacementFailure failure;
                        if (isShip)
                            placedMulti = shipEngine!.PlaceShip(_character, multiId, pos,
                                (Direction)((multiId & 0x3) * 2), out failure,
                                magic: deedItem.IsAttr(ObjAttributes.Magic))?.MultiItem;
                        else
                            placedMulti = _housingEngine.PlaceHouse(_character, multiId, pos, out failure, customFoundation,
                                magic: deedItem.IsAttr(ObjAttributes.Magic))?.MultiItem;
                        if (placedMulti != null)
                        {
                            placedMulti.Hue = deedItem.Hue;
                            if (deedItem.IsAttr(ObjAttributes.Magic))
                                placedMulti.SetAttr(ObjAttributes.Magic);
                            RestoreRedeededMultiUuid(deedItem, placedMulti,
                                isShip ? "SHIP_MULTI_UUID" : "HOUSE_MULTI_UUID");
                            SysMessage(isShip ? "Ship placed." : ServerMessages.Get("house_placed"));
                            if (_triggerDispatcher?.FireItemTrigger(deedItem, ItemTrigger.Destroy,
                                    new TriggerArgs { CharSrc = _character, ItemSrc = deedItem }) != TriggerResult.True)
                            {
                                _world.RemoveItem(deedItem);
                            }
                        }
                        else
                        {
                            SysMessage(PlacementFailureMessage(failure, isShip));
                        }
                    };

                    // B8: raise the 0x99 multi-preview cursor when the footprint is
                    // known (the client ghosts the house at the cursor, Source-X
                    // addTargetItems); fall back to the plain ground cursor otherwise.
                    if (multiDef != null)
                        SetPendingMultiTarget(placeCallback, multiId,
                            xOff: 0, yOff: anchorBottom, zOff: 0, hue: deedItem.Hue.Value);
                    else
                        SetPendingTarget(placeCallback);
                }
                break;

            // ---- BankBox / VendorBox: anti-cheat reject ----
            // ---- light sources ----
            case ItemType.LightLit:
                item.ItemType = ItemType.LightOut;
                _netState.Send(new PacketSound(0x0047, _character.X, _character.Y, _character.Z));
                BroadcastNearby?.Invoke(item.Position, UpdateRange,
                    new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                        item.X, item.Y, item.Z, item.Hue), 0);
                break;
            case ItemType.LightOut:
            {
                // Can't light a torch/lantern while it sits inside a container
                // (Source-X CItem::Use_Light rule).
                if (item.ContainedIn.IsValid && _world.FindObject(item.ContainedIn) is Item)
                {
                    SysMessage("You cannot light that while it is in a container.");
                    break;
                }
                // Source-X Use_Light: a burned-out source can never relight;
                // charges default to 20 when unset and burn down ONE PER MINUTE
                // via the lit-timer tick (Item.OnLightBurnTick), not per lighting.
                int charges = 20;
                if (item.TryGetTag("LIGHT_CHARGES", out string? cs) && int.TryParse(cs, out int c))
                    charges = c;
                if (charges <= 0 || item.TryGetTag("LIGHT_BURNED", out _))
                {
                    SysMessage("It has burned out and cannot be lit.");
                    break;
                }
                item.SetTag("LIGHT_CHARGES", charges.ToString());
                item.ItemType = ItemType.LightLit;
                item.SetTimeout(Environment.TickCount64 + Item.LightBurnTickMs);
                _netState.Send(new PacketSound(0x0047, _character.X, _character.Y, _character.Z));
                BroadcastNearby?.Invoke(item.Position, UpdateRange,
                    new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                        item.X, item.Y, item.Z, item.Hue), 0);
                break;
            }

            // ---- telepad / switch ----
            case ItemType.Telepad:
            {
                var dest = item.MoreP;
                if ((dest.X != 0 || dest.Y != 0) && IsValidTeleportDest(dest))
                {
                    _character.MoveTo(dest);
                    SendSelfRedraw();
                    _netState.Send(new PacketSound(0x01FE, _character.X, _character.Y, _character.Z));
                }
                break;
            }
            case ItemType.Switch:
                // Toggle the lever graphic (Source-X SetSwitchState): swap BaseId
                // with the alternate held in MORE1 so the lever visibly flips.
                if (item.More1 != 0)
                {
                    ushort altGfx = (ushort)item.More1;
                    item.More1 = item.BaseId;
                    item.BaseId = altGfx;
                    BroadcastNearby?.Invoke(item.Position, UpdateRange,
                        new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                            item.X, item.Y, item.Z, item.Hue), 0);
                    _netState.Send(new PacketSound(0x0F, _character.X, _character.Y, _character.Z));
                }
                // ARGN1 = fStanding (Source-X @Step contract): 1 — the char is
                // standing at/using the item rather than walking onto it.
                _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item, N1 = 1 });
                break;

            // ---- beverages ----
            case ItemType.Booze:
                // Source-X IT_BOOZE routes through Use_Drink: refuse an unmovable
                // fixture (a placed keg/barrel) instead of destroying it.
                if (!ItemMoveRules.CanMove(_character, item, out _))
                {
                    SysMessage(ServerMessages.Get(Msg.DrinkCantmove));
                    break;
                }
                // @Eat (Source-X) — drinking also feeds the @Eat hook. N1 = hunger.
                if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Eat,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item, N1 = 2 }) == TriggerResult.True)
                    break;
                _character.Food = (ushort)Math.Min(_character.Food + 2, 60);
                SysMessage("*hic!*");
                // Consume exactly one bottle (Use_Drink wConsume=1), never the whole
                // stack — a single drink used to delete every ale in the pile.
                ConsumeOneOnUse(item);
                break;

            // ---- musical instruments ----
            case ItemType.Musical:
                RouteSkillTarget(SkillType.Musicianship, item.Uid);
                break;

            // ---- figurine (pet shrink/unshrink) ----
            case ItemType.Figurine:
            {
                // Snapshot figurine (Source-X pet shrink/restore): recreate the stored
                // pet beside the player and consume the figurine.
                if (SphereNet.Game.NPCs.PetFigurine.IsPetFigurine(item))
                {
                    var restored = SphereNet.Game.NPCs.PetFigurine.Restore(
                        _character, item, _world, _character.Position);
                    if (restored != null)
                    {
                        _netState.Send(new PacketDeleteObject(item.Uid.Value));
                        SysMessage("Your pet materializes beside you.");
                    }
                    else
                    {
                        SysMessage("You have too many followers to restore that now.");
                    }
                    break;
                }

                // Legacy Source-X figurines store the pet in MORE1 and the
                // figurine owner in LINK. A copied/borrowed figurine must not
                // transfer somebody else's pet to the user.
                if (item.Link.IsValid && item.Link != _character.Uid &&
                    _character.PrivLevel < PrivLevel.GM)
                {
                    SysMessage(ServerMessages.Get(Msg.MsgFigurineNotyours));
                    break;
                }

                uint linkedSerial = item.More1;
                if (linkedSerial != 0)
                {
                    var pet = _world.FindChar(new Serial(linkedSerial));
                    if (pet != null && !pet.IsDeleted && !pet.IsPlayer)
                    {
                        if (!pet.TryAssignOwnership(_character, _character,
                                summoned: false, enforceFollowerCap: true))
                        {
                            SysMessage("You have too many followers to restore that now.");
                            break;
                        }
                        if (!_world.PlaceCharacter(pet, _character.Position))
                        {
                            SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
                            break;
                        }
                        pet.ClearStatFlag(StatFlag.Ridden);
                        _world.RemoveItem(item);
                        SysMessage("Your pet materializes beside you.");
                    }
                    else
                    {
                        SysMessage("The creature is lost.");
                    }
                }
                else
                {
                    SysMessage(ServerMessages.Get(Msg.MsgFigurineNotyours));
                }
                break;
            }

            // ---- moongate ----
            case ItemType.Moongate:
            {
                var dest = item.MoreP;
                if ((dest.X != 0 || dest.Y != 0) && IsValidTeleportDest(dest))
                {
                    _character.MoveTo(dest);
                    SendSelfRedraw();
                    _netState.Send(new PacketSound(0x01FE, _character.X, _character.Y, _character.Z));
                    _netState.Send(new PacketEffect(2, 0, 0, 0x3728,
                        _character.X, _character.Y, (short)_character.Z,
                        _character.X, _character.Y, (short)_character.Z,
                        10, 30, true, false));
                }
                else
                {
                    _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, N1 = 1 });
                }
                break;
            }

            // ---- training dummies ----
            case ItemType.TrainDummy:
            {
                ushort animId = (ushort)(item.BaseId == 0x1070 || item.BaseId == 0x1074 ? item.BaseId + 1 : item.BaseId);
                _netState.Send(new PacketSound(0x03B5, item.X, item.Y, item.Z));
                var skill = _character.GetEquippedItem(Layer.TwoHanded) != null
                    ? SkillType.Swordsmanship
                    : SkillType.Wrestling;
                RouteSkillTarget(skill, item.Uid);
                break;
            }
            case ItemType.TrainPickpocket:
                RouteSkillTarget(SkillType.Stealing, item.Uid);
                break;
            case ItemType.ArcheryButte:
                RouteSkillTarget(SkillType.Archery, item.Uid);
                break;

            // ---- kindling / bedroll / campfire ----
            case ItemType.Kindling:
                RouteSkillTarget(SkillType.Camping, item.Uid);
                break;
            case ItemType.Bedroll:
                SysMessage("You lay out the bedroll.");
                RouteSkillTarget(SkillType.Camping, item.Uid);
                break;
            case ItemType.Campfire:
                SysMessage("The fire is warm.");
                break;

            // ---- crafting stations (overridable via @DClick trigger) ----
            case ItemType.SpinWheel:
                // Cosmetic spinning-wheel sound (Source-X plays a spin anim on
                // dclick), then open the tailoring gump for actual crafting.
                BroadcastNearby?.Invoke(item.Position, UpdateRange,
                    new PacketSound(0x0055, item.X, item.Y, item.Z), 0);
                OpenCraftingGump(SkillType.Tailoring);
                break;
            case ItemType.Loom:
                OpenCraftingGump(SkillType.Tailoring);
                break;
            case ItemType.Anvil:
                OpenCraftingGump(SkillType.Blacksmithing);
                break;

            // ---- crops / foliage harvesting ----
            case ItemType.Crops:
            case ItemType.Foliage:
                HarvestPlant(item);
                break;

            // ---- beehive / seed / pitcher ----
            case ItemType.BeeHive:
                if (Random.Shared.Next(100) < 60)
                {
                    var honey = _world.CreateItem();
                    honey.BaseId = 0x09EC; // jar of honey
                    honey.ItemType = ItemType.Food;
                    honey.Name = "jar of honey";
                    PlaceItemInPack(_character, honey);
                    SysMessage("You gather some honey from the hive.");
                }
                else
                {
                    _character.ApplyPoison(1); // lesser poison from bee stings
                    SysMessage("You are stung by angry bees!");
                }
                break;
            case ItemType.Seed:
                SysMessage("Select where to plant the seed.");
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => PlantSeed(item, x, y, z));
                break;
            case ItemType.Pitcher:
                UsePotion(item);
                break;
            case ItemType.PitcherEmpty:
                SysMessage("Select a water source to fill the pitcher.");
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => FillPitcher(item, x, y));
                break;

            // ---- raw materials ----
            case ItemType.Cotton:
            case ItemType.Wool:
                // Source-X IT_WOOL/IT_COTTON: target a spinning wheel — wool
                // spins into 3 balls of yarn, cotton into 6 spools of thread.
                SysMessage("Select the spinning wheel to spin this on.");
                SetPendingItemTarget(item, (serial, x, y, z, gfx) =>
                    SpinMaterial(item, new Serial(serial)));
                break;
            case ItemType.Feather:
            case ItemType.Fur:
                SysMessage("Use a spinning wheel to process this material.");
                break;
            case ItemType.Thread:
            case ItemType.Yarn:
                // Source-X IT_THREAD/IT_YARN: target a loom — the loom
                // accumulates material (MORE1 type / MORE2 units) until a
                // bolt of cloth is finished.
                SysMessage("Select the loom to weave this on.");
                SetPendingItemTarget(item, (serial, x, y, z, gfx) =>
                    WeaveOnLoom(item, new Serial(serial)));
                break;
            case ItemType.Log:
            case ItemType.Board:
                SysMessage("Use a carpentry tool to craft with this.");
                break;
            case ItemType.Shaft:
                SysMessage("Use fletching tools to craft with this.");
                break;
            case ItemType.Bone:
                SysMessage("You examine the bone.");
                break;
            case ItemType.Rope:
                SysMessage("You examine the rope.");
                break;

            // ---- food variants ----
            case ItemType.FoodRaw:
            case ItemType.MeatRaw:
                SysMessage("This must be cooked first.");
                break;

            // ---- comm crystal ----
            case ItemType.CommCrystal:
                // Source-X CItemCommCrystal: double-clicking opens a target cursor;
                // the target must be another comm crystal, which becomes this
                // crystal's relay partner (m_uidLink). Speech near this crystal is
                // then relayed to the linked one (SpeechEngine.OnItemHear).
                SysMessage("Target the communication crystal to link to.");
                _client.SetPendingTarget((serial, _, _, _, _) =>
                {
                    var partner = _world.FindItem(new Serial(serial));
                    if (partner == null || partner.ItemType != ItemType.CommCrystal)
                    {
                        SysMessage("That is not a communication crystal.");
                        return;
                    }
                    if (partner == item)
                    {
                        SysMessage("That is the same crystal.");
                        return;
                    }
                    item.Link = partner.Uid;
                    SysMessage("Linked.");
                }, 0);
                break;

            // ---- portcullis ----
            case ItemType.Portculis:
            case ItemType.PortLocked:
                ToggleDoor(item);
                break;

            // ---- fletching tool ----
            case ItemType.Fletching:
                OpenCraftingGump(SkillType.Bowcraft);
                break;

            case ItemType.EqBankBox:
            case ItemType.EqVendorBox:
                _logger.LogWarning("Suspicious dclick on bankbox/vendorbox uid={Uid}", item.Uid.Value);
                break;

            // ---- pure wearables (clothing / armor / shield / jewelry) ----
            // Their entire "use" is being worn, which the equip gate above
            // already handled on double-click. Without these cases they fall
            // through to default and emit a spurious "you can't think of a way
            // to use that" right after successfully equipping.
            case ItemType.Clothing:
            case ItemType.Armor:
            case ItemType.ArmorLeather:
            case ItemType.ArmorChain:
            case ItemType.ArmorRing:
            case ItemType.ArmorBone:
            case ItemType.Shield:
            case ItemType.Jewelry:
            // The bow/crossbow/throwing/whip family equips to a hand layer via the
            // preamble too (Source-X routes them to ItemEquip only, no message), so
            // they belong in the silent break rather than the "can't think" default.
            case ItemType.WeaponBow:
            case ItemType.WeaponXBow:
            case ItemType.WeaponThrowing:
            case ItemType.WeaponWhip:
                break;

            default:
                if (DoorHelper.IsDoorItem(item, _world.MapData))
                {
                    if (item.ItemType == ItemType.DoorLocked)
                        SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                    else
                        ToggleDoor(item);
                    break;
                }
                if (TryToggleNearestMapStaticDoor(0))
                    break;
                SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
                break;
        }
    }

    /// <summary>Harvest a crop/foliage plant (Source-X CItemPlant::Plant_Use).
    /// The fruit item id comes from the plant's ITEMDEF TDATA3 (numeric or a
    /// defname). Reaping starts a regrow cooldown.</summary>
    private void HarvestPlant(Item item)
    {
        if (_character == null) return;

        // A regrowing (invisible) plot has nothing to reap yet.
        if (item.IsAttr(ObjAttributes.Invis))
        {
            SysMessage("There is nothing to harvest yet.");
            return;
        }

        var def = DefinitionLoader.GetItemDef(item.BaseId);
        ushort fruitId = 0;
        if (def != null)
        {
            if (def.TData3 != 0) fruitId = (ushort)def.TData3;
            else if (!string.IsNullOrEmpty(def.TData3Name) && Item.ResolveDefName != null)
                fruitId = Item.ResolveDefName(def.TData3Name);
        }
        if (fruitId == 0)
        {
            SysMessage("There is nothing to harvest yet.");
            return;
        }

        var fruit = _world.CreateItem();
        fruit.BaseId = fruitId;
        fruit.ItemType = ItemType.Food;
        PlaceItemInPack(_character, fruit);

        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketAnimation(_character.Uid.Value, (ushort)AnimationType.Bow), 0);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketSound(0x013E, _character.X, _character.Y, _character.Z), 0);

        // Source-X Plant_Use: reaping resets the crop to its first stage and regrows
        // it (hidden until the growth timer brings it back), instead of a flat cooldown.
        item.PlantCropReset();
        SysMessage("You harvest the plant.");
    }

    /// <summary>Fill an empty pitcher from a water source (Source-X
    /// CChar::Use_Item on IT_PITCHER_EMPTY).</summary>
    private void FillPitcher(Item pitcher, short x, short y)
    {
        if (_character == null) return;
        var here = new Point3D(x, y, _character.Z, _character.MapIndex);
        if (_character.Position.GetDistanceTo(here) > 3)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }
        var md = _world.MapData;
        bool water = md != null &&
            md.GetLandTileData(md.GetTerrainTile(_character.MapIndex, x, y).TileId).IsWet;
        if (!water)
        {
            SysMessage("That is not a water source.");
            return;
        }
        var def = DefinitionLoader.GetItemDef(pitcher.BaseId);
        ushort fullId = def != null && def.TData1 != 0 ? (ushort)def.TData1 : (ushort)0x1F9D;
        pitcher.BaseId = fullId;
        pitcher.ItemType = ItemType.Pitcher;
        if (pitcher.ContainedIn.IsValid)
            _netState.Send(new PacketContainerItem(
                pitcher.Uid.Value, pitcher.DispIdFull, 0, pitcher.Amount, pitcher.X, pitcher.Y,
                pitcher.ContainedIn.Value, pitcher.Hue, _netState.IsClientPost6017));
        else
            SendWorldItem(pitcher);
        SysMessage("You fill the pitcher with water.");
    }

    /// <summary>Plant a seed on the targeted ground (Source-X CChar::Use_Seed).
    /// The crop to grow comes from the seed's ITEMDEF TDATA1.</summary>
    private void PlantSeed(Item seed, short x, short y, sbyte z)
    {
        if (_character == null) return;
        var here = new Point3D(x, y, _character.Z, _character.MapIndex);
        if (_character.Position.GetDistanceTo(here) > 3)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }

        var def = DefinitionLoader.GetItemDef(seed.BaseId);
        ushort cropId = 0;
        if (def != null)
        {
            if (def.TData1 != 0) cropId = (ushort)def.TData1;
            else if (!string.IsNullOrEmpty(def.TData1Name) && Item.ResolveDefName != null)
                cropId = Item.ResolveDefName(def.TData1Name);
        }
        if (cropId == 0)
        {
            SysMessage("You cannot plant that here.");
            return;
        }

        var crop = _world.CreateItem();
        crop.BaseId = cropId;
        crop.ItemType = ItemType.Crops;
        _world.PlaceItem(crop, new Point3D(x, y, z, _character.MapIndex));
        crop.PlantStartGrowth(); // Source-X Use_Seed → the crop begins its growth chain
        BroadcastNearby?.Invoke(crop.Position, UpdateRange,
            new PacketWorldItem(crop.Uid.Value, crop.DispIdFull, crop.Amount,
                crop.X, crop.Y, crop.Z, crop.Hue), 0);

        if (seed.Amount > 1) seed.Amount--; else _world.RemoveItem(seed);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketAnimation(_character.Uid.Value, (ushort)AnimationType.Bow), 0);
        SysMessage("You plant the seed.");
    }

    // ---- helpers used by HandleItemUse target callbacks ----

    /// <summary>Source-X arrow/bolt presence check before ranged swing.</summary>
    internal bool HasAmmoInBackpack(ItemType ammo)
    {
        if (_character?.Backpack == null) return false;
        foreach (var it in _character.Backpack.Contents)
            if (it.ItemType == ammo && it.Amount > 0) return true;
        return false;
    }

    /// <summary>Consume one arrow/bolt from the backpack. Source-X Fight_Hit ammo burn.</summary>
    internal void ConsumeAmmoFromBackpack(ItemType ammo)
    {
        if (_character?.Backpack == null) return;
        foreach (var it in _character.Backpack.Contents)
        {
            if (it.ItemType != ammo || it.Amount <= 0) continue;
            if (it.Amount <= 1)
                _world.RemoveItem(it);
            else
                it.Amount = (ushort)(it.Amount - 1);
            return;
        }
    }

    /// <summary>Find a key in the player's backpack that opens a locked container/door.</summary>
    private Character? ResolveContainerOwner(Item item, int maxDepth = 16)
    {
        var current = item;
        for (int i = 0; i < maxDepth && current != null; i++)
        {
            if (!current.ContainedIn.IsValid) return null;
            var holder = _world.FindObject(current.ContainedIn);
            if (holder is Character c) return c;
            if (holder is Item parent) { current = parent; continue; }
            return null;
        }
        return null;
    }

    /// <summary>Source-X CClient::addDrawMap: display the map gump (0x90) and
    /// replay the stored pins (0x56 draw-pin per PIN_n tag). A blank map —
    /// t_map_blank or an empty/invalid world rect — only reports blank.
    /// Rect words follow the CItem m_itMap layout: MORE1 = top(lo)/left(hi),
    /// MORE2 = bottom(lo)/right(hi).</summary>
    private void OpenMapGump(Item item)
    {
        ushort top = (ushort)(item.More1 & 0xFFFF);
        ushort left = (ushort)(item.More1 >> 16);
        ushort bottom = (ushort)(item.More2 & 0xFFFF);
        ushort right = (ushort)(item.More2 >> 16);
        if (item.ItemType == ItemType.MapBlank || right <= left || bottom <= top)
        {
            SysMessage("This map is blank.");
            return;
        }

        _netState.Send(new PacketMapDisplay(item.Uid.Value, left, top, right, bottom));
        // Source-X addMapMode(MAP_UNSENT): reset the client's pin list before
        // replaying ours, plot mode off.
        _netState.Send(new PacketMapPlot(item.Uid.Value, 5, false));
        for (int i = 1; ; i++)
        {
            string? pin = item.Tags.Get($"PIN_{i}");
            if (string.IsNullOrEmpty(pin)) break;
            var parts = pin.Split(',');
            if (parts.Length == 2 &&
                ushort.TryParse(parts[0], out ushort px) && ushort.TryParse(parts[1], out ushort py))
            {
                _netState.Send(new PacketMapPlot(item.Uid.Value, 1, false, px, py));
            }
        }
    }

    /// <summary>UO sextant math (Source-X Use_Sextant): degrees/minutes from
    /// the world center 1323,1624 across the 5120x4096 wrap plane.</summary>
    internal static string FormatSextant(Point3D p)
    {
        const int xCenter = 1323, yCenter = 1624, xWidth = 5120, yHeight = 4096;
        double absLong = (double)((p.X - xCenter) * 360) / xWidth;
        double absLat = (double)((p.Y - yCenter) * 360) / yHeight;
        if (absLong > 180.0) absLong = -180.0 + (absLong % 180.0);
        if (absLong < -180.0) absLong = 180.0 + (absLong % 180.0);
        if (absLat > 180.0) absLat = -180.0 + (absLat % 180.0);
        if (absLat < -180.0) absLat = 180.0 + (absLat % 180.0);
        bool east = absLong >= 0, south = absLat >= 0;
        absLong = Math.Abs(absLong);
        absLat = Math.Abs(absLat);
        int xLong = (int)absLong, yLat = (int)absLat;
        int xMins = (int)(absLong % 1.0 * 60), yMins = (int)(absLat % 1.0 * 60);
        return $"{yLat}° {yMins}'{(south ? "S" : "N")}, {xLong}° {xMins}'{(east ? "E" : "W")}";
    }

    private Item? FindBackpackKeyFor(Item locked)
    {
        if (_character?.Backpack == null) return null;
        uint linkId = locked.Uid.Value;
        // Source-X lock codes: a key matches the locked item itself OR the
        // structure the lock belongs to (house door → the multi) — the house
        // key's code is the multi uid, mirrored on every component's Link.
        uint structId = locked.Link.IsValid ? locked.Link.Value : 0;
        foreach (var it in EnumerateContents(_character.Backpack, 0))
        {
            if (it.ItemType is not (ItemType.Key or ItemType.Keyring)) continue;
            uint kv = it.Link.IsValid ? it.Link.Value : 0;
            if (kv == 0 && it.TryGetTag("LINK", out string? lk))
                uint.TryParse(lk, out kv);
            if (kv != 0 &&
                (kv == linkId || (structId != 0 && kv == structId)))
                return it;
        }
        return null;

        static IEnumerable<Item> EnumerateContents(Item container, int depth)
        {
            if (depth >= 16) yield break;
            foreach (var child in container.Contents)
            {
                yield return child;
                foreach (var nested in EnumerateContents(child, depth + 1))
                    yield return nested;
            }
        }
    }

    /// <summary>Re-enter the active-skill pipeline with a pre-resolved Serial target.</summary>
    private void RouteSkillTarget(SkillType skill, Serial target, Point3D? point = null)
    {
        if (_character == null) return;
        var obj = target.IsValid ? _world.FindObject(target) : null;
        var sink = new GameClient.InfoSkillSink(_client, _character);
        _skillHandlers?.UseActiveSkill(sink, skill, obj, point);
    }

    private static bool IsWeaponItemType(ItemType type) => type is
        ItemType.WeaponSword or ItemType.WeaponFence or ItemType.WeaponAxe or
        ItemType.WeaponMaceSharp or ItemType.WeaponMaceStaff or ItemType.WeaponMaceSmith or
        ItemType.WeaponBow or ItemType.WeaponXBow or ItemType.WeaponMaceCrook or
        ItemType.WeaponMacePick or ItemType.WeaponThrowing or ItemType.WeaponWhip;

    private bool CanReachTargetItem(Item? obj)
    {
        if (obj == null || _character == null) return false;
        var topCont = GetTopContainer(obj);
        if (topCont == null) return false;

        if (topCont.ContainedIn.IsValid)
        {
            var wearer = _world.FindChar(topCont.ContainedIn);
            if (wearer != null)
            {
                if (wearer == _character) return true;
                if (wearer.MapIndex != _character.MapIndex) return false;
                if (_character.PrivLevel >= PrivLevel.GM) return true;
                return _character.Position.GetDistanceTo(wearer.Position) <= 3 &&
                    _world.CanSeeLOS(_character.Position, wearer.Position);
            }
        }

        Point3D point = topCont.Position;
        if (point.Map != _character.MapIndex) return false;
        if (_character.PrivLevel >= PrivLevel.GM) return true;
        return _character.Position.GetDistanceTo(point) <= 3 &&
            _world.CanSeeLOS(_character.Position, point);
    }

    private void DetachFromItemSpawner(Item item)
    {
        if (!item.TryGetTag("SPAWN_POINT_UUID", out string? raw) ||
            !Guid.TryParse(raw, out Guid spawnUuid) ||
            _world.FindByUuid(spawnUuid) is not Item spawnItem ||
            spawnItem.SpawnItem == null)
            return;

        spawnItem.SpawnItem.DelObj(item.Uid);
        item.RemoveTag("SPAWN_POINT_UUID");
    }

    /// <summary>Script entry for the item SMELT verb (Source-X CIV_SMELT):
    /// same flow as the targeted smelt, forge supplied by the script.</summary>
    internal void SmeltFromScript(Item ore, Serial forgeUid) => HandleSmeltTarget(ore, forgeUid);

    private void HandleSmeltTarget(Item ore, Serial target)
    {
        if (_character == null) return;
        if (ore.IsDeleted || ore.ItemType != ItemType.Ore)
        {
            SysMessage(ServerMessages.Get(Msg.MiningNotOre));
            return;
        }

        var forge = target.IsValid ? _world.FindItem(target) : null;
        if (forge == null || forge.ItemType != ItemType.Forge || !CanReachTargetItem(forge))
        {
            SysMessage(ServerMessages.Get(Msg.MiningForge));
            return;
        }

        if (!CanReachTargetItem(ore))
        {
            SysMessage(ServerMessages.Get(Msg.MiningReach));
            return;
        }

        int amount = Math.Max(1, (int)ore.Amount);
        if (_triggerDispatcher?.FireItemTrigger(ore, ItemTrigger.Smelt, new TriggerArgs
        {
            CharSrc = _character,
            ItemSrc = ore,
            O1 = forge,
            N1 = amount,
            S1 = ServerMessages.Get(Msg.MiningSmelt),
        }) == TriggerResult.True)
            return;

        if (!SkillEngine.UseQuick(_character, SkillType.Mining, 30))
        {
            ConsumeOreStack(ore);
            SysMessage(ServerMessages.GetFormatted(Msg.MiningNothing, ore.GetName()));
            return;
        }

        var oreHue = ore.Hue;
        ushort ingotId = ResolveSmeltIngotId(ore);
        ConsumeOreStack(ore);

        var ingot = _world.CreateItem();
        ingot.BaseId = ingotId;
        ingot.ItemType = ItemType.Ingot;
        // Carry the ore's hue onto the ingot so a coloured/special ore (valorite,
        // verite, …) smelts to its matching coloured ingot instead of always
        // becoming plain iron — coloured ingots share the iron ingot graphic and
        // differ only by hue.
        ingot.Hue = oreHue;
        var ingotDef = DefinitionLoader.GetItemDef(ingotId);
        ingot.Name = ingotDef != null && !string.IsNullOrWhiteSpace(ingotDef.Name)
            ? DefinitionLoader.ResolveNames(ingotDef.Name)
            : (oreHue.Value != 0 ? "ingot" : "iron ingot");
        ingot.Amount = (ushort)Math.Min(amount, ushort.MaxValue);

        var pack = _character.Backpack;
        if (pack != null && (_character.PrivLevel >= PrivLevel.GM || _character.CanCarry(ingot)))
        {
            var actual = pack.TryAddItemWithStack(ingot);
            if (actual != null && actual != ingot)
                _world.RemoveItem(ingot);

            if (actual != null)
            {
                _netState.Send(new PacketContainerItem(
                    actual.Uid.Value, actual.DispIdFull, 0,
                    actual.Amount, actual.X, actual.Y,
                    pack.Uid.Value, actual.Hue,
                    _netState.IsClientPost6017));

                _triggerDispatcher?.FireItemTrigger(actual, ItemTrigger.Create,
                    new TriggerArgs { CharSrc = _character, ItemSrc = actual });
                return;
            }
        }

        _world.PlaceItemWithDecay(ingot, _character.Position);
        _triggerDispatcher?.FireItemTrigger(ingot, ItemTrigger.Create,
            new TriggerArgs { CharSrc = _character, ItemSrc = ingot });
    }

    /// <summary>Resolve the ingot id an ore smelts into. Custom ore can name a
    /// non-standard ingot via TAG.SMELT_TO on the ore item or its itemdef; standard
    /// ore falls back to the iron ingot graphic (0x1BF2), the ore hue is carried
    /// over by the caller so coloured ore yields its coloured ingot.</summary>
    private static ushort ResolveSmeltIngotId(Item ore)
    {
        string? raw = null;
        if (ore.TryGetTag("SMELT_TO", out string? itemTag) && !string.IsNullOrWhiteSpace(itemTag))
            raw = itemTag;
        else
        {
            var def = DefinitionLoader.GetItemDef(ore.BaseId);
            if (def != null && def.TagDefs.Has("SMELT_TO"))
                raw = def.TagDefs.Get("SMELT_TO");
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            raw = raw.Trim();
            bool ok = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort id)
                : ushort.TryParse(raw, out id);
            if (ok && id != 0)
                return id;
        }
        return 0x1BF2;
    }

    private void ConsumeOreStack(Item ore)
    {
        if (ore.ContainedIn.IsValid)
            _netState.Send(new PacketDeleteObject(ore.Uid.Value));
        else
            BroadcastDeleteObject(ore.Uid.Value);

        _world.RemoveItem(ore);
    }

    /// <summary>Source-X uses scissors to convert hides/cloth to leather/bolts.</summary>
    private void HandleScissorsTarget(Item scissors, Serial target)
    {
        if (_character == null) return;
        var obj = target.IsValid ? _world.FindObject(target) as Item : null;
        if (obj == null || !CanReachTargetItem(obj)) { SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); return; }
        // A locked-down / Move_Never fixture (a placed cloth/hide decoration) must
        // not be cut or type-converted by scissors.
        if (!ItemMoveRules.CanMove(_character, obj, out _)) { SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); return; }
        switch (obj.ItemType)
        {
            case ItemType.Hide: obj.ItemType = ItemType.Leather; SysMessage("You cut the hide into leather."); break;
            case ItemType.Cloth: obj.ItemType = ItemType.ClothBolt; SysMessage("You cut the cloth into bolts."); break;
            // Clean the bloody bandages (whole stack retained) rather than deleting
            // them — the message says "clean", not "destroy".
            case ItemType.BandageBlood: obj.ItemType = ItemType.Bandage; SysMessage(ServerMessages.Get(Msg.ItemuseBandageClean)); break;
            default: SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); break;
        }
    }

    /// <summary>Source-X blade-on-corpse: carving through DeathEngine.CarveCorpse —
    /// the engine existed but no player input path reached it (audit #2).</summary>
    private void CarveCorpseWithBlade(Item corpse)
    {
        if (_character == null) return;
        if (_character.PrivLevel < PrivLevel.GM && !CanReachTargetItem(corpse))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }
        var death = _client.DeathEng;
        if (death == null) return;
        var parts = death.CarveCorpse(_character, corpse);
        SysMessage(parts.Count > 0
            ? "You carve the corpse."
            : "There is nothing left to carve.");
    }

    /// <summary>Source-X blade-on-sheep (CREID_SHEEP 0x00CF → sheared 0x00DF):
    /// yields wool and swaps the body; regrowth stays with the NPC's own
    /// script/respawn cycle.</summary>
    private void ShearSheep(Character sheep)
    {
        if (_character == null) return;
        if (sheep.IsDead || sheep.BodyId != 0x00CF) return;
        if (_character.PrivLevel < PrivLevel.GM &&
            _character.Position.GetDistanceTo(sheep.Position) > 3)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }
        var wool = _world.CreateItem();
        wool.BaseId = 0x0DF8; // i_wool
        wool.Amount = 2;
        PlaceItemInPack(_character, wool);
        sheep.BodyId = 0x00DF; // sheared sheep
        SysMessage("You shear the sheep and collect the wool.");
    }

    /// <summary>Source-X blade-on-dead-fish: fillet into raw fish steaks.</summary>
    private void FilletFish(Item fish)
    {
        if (_character == null) return;
        if (_character.PrivLevel < PrivLevel.GM && !CanReachTargetItem(fish))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }
        int count = Math.Max(1, (int)fish.Amount);
        var steaks = _world.CreateItem();
        steaks.BaseId = 0x097A; // raw fish steak
        steaks.ItemType = ItemType.Food;
        steaks.Amount = (ushort)Math.Min(ushort.MaxValue, count * 4);
        PlaceItemInPack(_character, steaks);
        _world.RemoveItem(fish);
        SysMessage("You cut the fish into raw fish steaks.");
    }

    /// <summary>Source-X Use_Cannon_Feed: sulfurous ash loads powder (MORE1
    /// bit 1), a cannon ball loads shot (bit 2); one unit consumed per load.</summary>
    private void FeedCannon(Item cannon, Item? feed)
    {
        if (_character == null) return;
        if (feed == null || feed.IsDeleted)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseCannonEmpty));
            return;
        }

        if (feed.BaseId == 0x0F8C) // i_reag_sulfur_ash (ITEMID_REAG_SA)
        {
            if ((cannon.More1 & 1) != 0)
            {
                SysMessage(ServerMessages.Get(Msg.ItemuseCannonHpowder));
                return;
            }
            cannon.More1 |= 1;
            ConsumeOneFrom(feed);
            SysMessage(ServerMessages.Get(Msg.ItemuseCannonLpowder));
            return;
        }

        if (feed.ItemType == ItemType.CannonBall)
        {
            if ((cannon.More1 & 2) != 0)
            {
                SysMessage(ServerMessages.Get(Msg.ItemuseCannonHshot));
                return;
            }
            cannon.More1 |= 2;
            ConsumeOneFrom(feed);
            SysMessage(ServerMessages.Get(Msg.ItemuseCannonLshot));
            return;
        }

        SysMessage(ServerMessages.Get(Msg.ItemuseCannonEmpty));
    }

    /// <summary>Source-X IT_CANNON_MUZZLE fire (CClientTarg): reset the load,
    /// boom + muzzle smoke, cannonball bolt to a target inside sight range,
    /// 80 + rand(150) blunt/fire damage to a char (an item target takes hull
    /// damage through its hitpoints).</summary>
    private void FireCannon(Item cannon, Serial targetSerial, short x, short y, sbyte z)
    {
        if (_character == null) return;
        cannon.More1 &= ~3u;

        BroadcastNearby?.Invoke(cannon.Position, UpdateRange,
            new PacketSound(0x0207, cannon.X, cannon.Y, cannon.Z), 0);
        var smoke = new PacketEffect(3, cannon.Uid.Value, cannon.Uid.Value, 0x3735,
            cannon.X, cannon.Y, cannon.Z, cannon.X, cannon.Y, cannon.Z, 9, 6, true, false);
        BroadcastNearby?.Invoke(cannon.Position, UpdateRange, smoke, 0);

        var targetObj = targetSerial.IsValid ? _world.FindObject(targetSerial) : null;
        if (targetObj == null)
            return; // ground shot — just the boom, like the reference

        if (cannon.Position.GetDistanceTo(targetObj.Position) > 14)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }

        var ball = new PacketEffect(0, cannon.Uid.Value, targetObj.Uid.Value, 0x0E73,
            cannon.X, cannon.Y, cannon.Z,
            targetObj.Position.X, targetObj.Position.Y, (short)targetObj.Position.Z,
            8, 0, false, true);
        BroadcastNearby?.Invoke(cannon.Position, UpdateRange, ball, 0);
        BroadcastNearby?.Invoke(targetObj.Position, UpdateRange,
            new PacketSound(0x0207, targetObj.Position.X, targetObj.Position.Y,
                (sbyte)targetObj.Position.Z), 0);

        int dmg = 80 + Random.Shared.Next(150);
        if (targetObj is Character victim)
        {
            if (victim.IsDead || CombatEngine.IsDamageImmune(victim))
                return;
            victim.Hits -= (short)Math.Min(dmg, victim.Hits);
            if (victim.Hits <= 0 && !victim.IsDead)
            {
                if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(victim, _character);
                else victim.Kill();
            }
        }
        else if (targetObj is Item itemTarget &&
                 !itemTarget.IsAttr(ObjAttributes.Static | ObjAttributes.Move_Never))
        {
            itemTarget.HitsCur -= dmg;
            if (itemTarget.HitsCur <= 0)
                _world.RemoveItem(itemTarget);
        }
    }

    private void ConsumeOneFrom(Item stack)
    {
        if (stack.Amount > 1) stack.Amount--;
        else _world.RemoveItem(stack);
    }

    /// <summary>Source-X IT_WOOL/IT_COTTON on a spinning wheel: consume one
    /// pile; wool yields 3 balls of yarn, cotton yields 6 spools of thread.</summary>
    private void SpinMaterial(Item material, Serial targetSerial)
    {
        if (_character == null) return;
        var wheel = targetSerial.IsValid ? _world.FindItem(targetSerial) : null;
        if (wheel == null || wheel.ItemType != ItemType.SpinWheel)
        {
            SysMessage("You must use that on a spinning wheel.");
            return;
        }
        if (_character.PrivLevel < PrivLevel.GM && !CanReachTargetItem(wheel))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }

        bool wool = material.ItemType == ItemType.Wool;
        ConsumeOneFrom(material);

        ushort madeId = Item.ResolveDefName?.Invoke(wool ? "i_yarn" : "i_thread") ?? 0;
        var made = _world.CreateItem();
        made.BaseId = madeId != 0 ? madeId : (wool ? (ushort)0x0E1D : (ushort)0x0FA0);
        made.ItemType = wool ? ItemType.Yarn : ItemType.Thread;
        made.Amount = wool ? (ushort)3 : (ushort)6;
        PlaceItemInPack(_character, made);
        SysMessage(ServerMessages.Get(wool ? "itemuse_wool_create" : "itemuse_cotton_create"));
    }

    /// <summary>Source-X IT_THREAD/IT_YARN on a loom: the loom stores the
    /// material type in MORE1 and the accumulated units in MORE2
    /// (m_itLoom.m_ridCloth / m_iClothQty); a different material ejects the
    /// stored partial weave, and at 4 units a bolt of cloth is produced.</summary>
    private void WeaveOnLoom(Item material, Serial targetSerial)
    {
        if (_character == null) return;
        var loom = targetSerial.IsValid ? _world.FindItem(targetSerial) : null;
        if (loom == null || loom.ItemType != ItemType.Loom)
        {
            SysMessage("You must use that on a loom.");
            return;
        }
        if (_character.PrivLevel < PrivLevel.GM && !CanReachTargetItem(loom))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }

        // A different material than the stored partial weave ejects it.
        if (loom.More1 != 0 && loom.More1 != material.DispIdFull)
        {
            var stored = _world.CreateItem();
            stored.BaseId = (ushort)loom.More1;
            stored.Amount = (ushort)Math.Max(1, (int)loom.More2);
            PlaceItemInPack(_character, stored);
            loom.More1 = 0;
            loom.More2 = 0;
            SysMessage(ServerMessages.Get("itemuse_loom_remove"));
            return;
        }
        loom.More1 = material.DispIdFull;

        const int need = 4; // units per bolt (Source-X sm_Txt_LoomUse - 1)
        int have = (int)loom.More2;
        int used = Math.Min(need - have, material.Amount);
        if (material.Amount > used) material.Amount -= (ushort)used;
        else _world.RemoveItem(material);

        if (have + used < need)
        {
            loom.More2 = (uint)(have + used);
            SysMessage(ServerMessages.Get($"itemuse_bolt_{have + used}"));
            return;
        }

        SysMessage(ServerMessages.Get("itemuse_bolt_5"));
        loom.More1 = 0;
        loom.More2 = 0;
        ushort boltId = Item.ResolveDefName?.Invoke("i_cloth_bolt") ?? 0;
        var bolt = _world.CreateItem();
        bolt.BaseId = boltId != 0 ? boltId : (ushort)0x0F95;
        bolt.Amount = 1;
        PlaceItemInPack(_character, bolt);
    }

    /// <summary>Source-X key use: link key, lock/unlock door or container.</summary>
    private void HandleKeyUse(Item key, Serial target)
    {
        var obj = target.IsValid ? _world.FindObject(target) as Item : null;
        if (obj == null || !CanReachTargetItem(obj)) { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNolock)); return; }

        bool linked = key.TryGetTag("LINK", out string? lk) && uint.TryParse(lk, out uint kv) && kv == obj.Uid.Value;
        if (!linked) { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNokey)); return; }

        if (obj.ItemType == ItemType.ContainerLocked) obj.ItemType = ItemType.Container;
        else if (obj.ItemType == ItemType.Container) obj.ItemType = ItemType.ContainerLocked;
        else if (obj.ItemType == ItemType.DoorLocked) obj.ItemType = ItemType.Door;
        else if (obj.ItemType == ItemType.Door) obj.ItemType = ItemType.DoorLocked;
        else { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNolock)); return; }
    }

    /// <summary>Pick a hue from a Dye onto a DyeVat (Source-X two-step).</summary>
    private void HandleDyePickup(Item dye, Serial target)
    {
        var vat = target.IsValid ? _world.FindObject(target) as Item : null;
        if (vat == null || vat.ItemType != ItemType.DyeVat || !CanReachTargetItem(vat))
        { SysMessage(ServerMessages.Get(Msg.ItemuseDyeFail)); return; }
        vat.SetTag("DYE_HUE", dye.Hue.ToString());
        SysMessage("You apply the dye to the vat.");
    }

    /// <summary>Apply a DyeVat hue to a target item.</summary>
    private void HandleDyeApply(Item vat, Serial target)
    {
        var dest = target.IsValid ? _world.FindObject(target) as Item : null;
        if (dest == null || !CanReachTargetItem(dest)) { SysMessage(ServerMessages.Get(Msg.ItemuseDyeReach)); return; }
        if (vat.TryGetTag("DYE_HUE", out string? hueText) && ushort.TryParse(hueText, out ushort hue))
        {
            if (_triggerDispatcher?.FireItemTrigger(dest, ItemTrigger.Dye, new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = dest,
                O1 = vat,
                N1 = hue
            }) == TriggerResult.True)
                return;

            dest.Hue = new Core.Types.Color(hue);
            // Broadcast the recolour to every nearby client. The view-delta only
            // tracks GROUND items, so a worn/equipped item dyed this way would
            // otherwise stay its old colour on observers (and self) until a full
            // resync. OnVisualUpdate → SendItemVisualUpdate emits 0x2E for worn
            // items, 0x1A for ground, 0x25 for the owner's pack.
            Item.OnVisualUpdate?.Invoke(dest);
            SysMessage("The item changes color.");
        }
    }

    private void ApplyHairDye(Item dye)
    {
        if (_character == null) return;
        ushort hue = dye.Hue.Value != 0 ? dye.Hue.Value : (ushort)0x044E;
        var hair = _character.GetEquippedItem(Layer.Hair);
        var beard = _character.GetEquippedItem(Layer.FacialHair);
        if (hair != null)
        {
            hair.Hue = new Core.Types.Color(hue);
            // Hair/beard are always worn (Layer.Hair/FacialHair) — the ground
            // view-delta never sees them, so broadcast the recolour explicitly
            // or it stays the old colour for everyone until a resync.
            Item.OnVisualUpdate?.Invoke(hair);
        }
        if (beard != null)
        {
            beard.Hue = new Core.Types.Color(hue);
            Item.OnVisualUpdate?.Invoke(beard);
        }
        SysMessage("You dye your hair.");
    }

    /// <summary>Format a Source-X-style local game time string for IT_CLOCK.</summary>
    private static string FormatLocalGameTime()
    {
        var now = DateTime.Now;
        return $"It is {now.Hour:00}:{now.Minute:00}.";
    }

    /// <summary>
    /// Source-X CChar::NPC_OnHearPetCmd parity. Recognises every PC_* verb
    /// from upstream (FOLLOW/GUARD/STAY/STOP/COME/ATTACK/KILL/FRIEND/UNFRIEND/
    /// TRANSFER/RELEASE/DROP/DROP ALL/EQUIP/STATUS/CASH/BOUGHT/SAMPLES/STOCK/
    /// PRICE/GO/SPEAK/GUARD ME/FOLLOW ME) and routes pets through the matching
    /// PetAIMode + DEFMSG_NPC_PET_* output. Returns true when the input was a
    /// pet command -- caller then suppresses normal speech broadcast.
    /// </summary>
    internal bool TryHandlePetCommand(string text)
    {
        if (_character == null) return false;
        string lower = text.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');

        // Pet command vocabulary table mirrors sm_Pet_table in Source-X.
        // Order matters because we longest-prefix match (e.g. "follow me" before "follow").
        ReadOnlySpan<string> vocab =
        [
            "all follow", "all guard", "all stay", "all stop", "all come",
            "all attack", "all kill", "all friend", "all unfriend", "all transfer",
            "all release", "all drop all", "all drop", "all equip", "all status",
            "all guard me", "all follow me", "all go", "all speak",
            "follow me", "guard me", "drop all"
        ];

        // "all <verb>" path.
        if (lower.StartsWith("all ", StringComparison.Ordinal))
        {
            string verb = NormalizePetVerb(lower[4..], allMode: true);
            if (!IsPetCommandVerb(verb))
                return false;
            return DispatchAllPets(verb);
        }

        // "<petname> <verb>" path -- longest-match verb.
        int spaceIdx = lower.IndexOf(' ');
        if (spaceIdx <= 0) return false;
        string name = lower[..spaceIdx];
        string rest = NormalizePetVerb(lower[(spaceIdx + 1)..], allMode: false);
        if (!IsPetCommandVerb(rest))
            return false;
        return DispatchNamedPet(name, rest);
    }

    private static string NormalizePetVerb(string rawVerb, bool allMode)
    {
        string verb = rawVerb.Trim().ToLowerInvariant().TrimEnd('.', '!', '?');
        verb = verb switch
        {
            "kills" => "kill",
            "attacks" => "attack",
            "comes" => "come",
            "follows" => "follow",
            _ => verb
        };

        // Source-style shortcut: "all follow" behaves like "all follow me".
        if (allMode && verb == "follow")
            return "follow me";
        return verb;
    }

    private static bool IsPetCommandVerb(string verb) => verb switch
    {
        "follow me" or "guard me" or "come" or "stay" or "stop" or "speak" or
        "drop" or "drop all" or "equip" or "status" or
        "attack" or "kill" or "guard" or "follow" or "go" or
        "friend" or "unfriend" or "transfer" or "release" or
        "price" or "bought" or "samples" or "stock" or "cash" or "shrink" => true,
        _ => false
    };

    /// <summary>Source-X PC_*: target a single pet by name prefix.</summary>
    private bool DispatchNamedPet(string namePrefix, string verb)
    {
        if (_character == null) return false;
        var pet = CollectCommandablePets(namePrefix).FirstOrDefault();
        if (pet == null)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        return ApplyPetVerb(pet, verb);
    }

    /// <summary>Source-X PC_*: broadcast verb to every nearby pet of mine.</summary>
    private bool DispatchAllPets(string verb)
    {
        if (_character == null) return false;
        var pets = CollectCommandablePets().ToList();
        if (pets.Count == 0)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        if (IsPetTargetVerb(verb))
        {
            EmitPetTargetPrompt(pets, verb);
            return true;
        }

        bool any = false;
        foreach (var pet in pets)
            if (ApplyPetVerb(pet, verb)) any = true;
        return any;
    }

    private static bool IsPetTargetVerb(string verb) => verb switch
    {
        // Source-X PC_* verbs that open a target cursor. bought/samples/stock/cash
        // are vendor management verbs that act immediately (open a container or
        // dispense the purse), NOT target verbs — they must not raise a cursor.
        "attack" or "kill" or "guard" or "follow" or "go" or
        "friend" or "unfriend" or "transfer" or "release" or
        "price" => true,
        _ => false
    };

    /// <summary>
    /// Apply a Source-X PC_* verb to a single pet, emitting the matching
    /// DEFMSG_NPC_PET_* message. Verbs that need a target store a pending
    /// callback so the next click resolves.
    /// </summary>
    private bool ApplyPetVerb(Character pet, string verb)
    {
        if (_character == null) return false;
        if (!pet.CanAcceptPetCommandFrom(_character))
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        // Source-X: conjured/summoned NPCs can't be transferred or friended
        if (pet.IsSummoned && verb is "transfer" or "friend" or "unfriend")
        {
            NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
            return true;
        }

        // Source-X: dead bonded pets accept only passive commands
        if (pet.IsDead)
        {
            bool allowed = verb is "follow me" or "come" or "stay" or "stop" or "follow";
            if (!allowed)
            {
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                return true;
            }
        }

        switch (verb)
        {
            case "follow me":
                pet.PetAIMode = PetAIMode.Follow;
                pet.FightTarget = Serial.Invalid; // an order calls the pet off its fight
                pet.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
                // @Follow (Source-X) — pet begins following its master. <src> = master.
                _triggerDispatcher?.FireCharTrigger(pet, CharTrigger.Follow,
                    new TriggerArgs { CharSrc = _character, O1 = _character });
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "come":
                pet.PetAIMode = PetAIMode.Come;
                pet.FightTarget = Serial.Invalid; // an order calls the pet off its fight
                pet.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
                _triggerDispatcher?.FireCharTrigger(pet, CharTrigger.Follow,
                    new TriggerArgs { CharSrc = _character, O1 = _character });
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "shrink":
            {
                // Source-X pet shrink: pack the pet into a figurine the player can
                // carry and later restore. The pet is removed from the world (its
                // deletion broadcasts to observers); the figurine appears in the pack.
                var pack = _character.Backpack;
                if (pack == null)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                    return true;
                }
                var figurine = _world.CreateItem();
                figurine.BaseId = 0x2106; // statuette graphic
                bool canPack = (_character.PrivLevel >= PrivLevel.GM || _character.CanCarry(figurine)) &&
                    pack.TryAddItem(figurine);
                if (!canPack)
                {
                    _world.RemoveItem(figurine);
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                    return true;
                }
                if (SphereNet.Game.NPCs.PetFigurine.Shrink(_character, pet, figurine, _world))
                {
                    _netState.Send(new PacketContainerItem(
                        figurine.Uid.Value, figurine.DispIdFull, 0, figurine.Amount,
                        figurine.X, figurine.Y, pack.Uid.Value, figurine.Hue,
                        _netState.IsClientPost6017));
                    SysMessage("Your pet has been packed into a figurine.");
                }
                else
                {
                    _world.RemoveItem(figurine);
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                }
                return true;
            }

            case "stay":
            case "stop":
                pet.PetAIMode = PetAIMode.Stay;
                pet.FightTarget = Serial.Invalid; // an order calls the pet off its fight
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "guard me":
                pet.PetAIMode = PetAIMode.Guard;
                pet.SetTag("GUARD_TARGET", _character.Uid.Value.ToString());
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "speak":
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "drop":
                if (pet.Backpack == null || pet.Backpack.Contents.Count == 0)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetCarrynothing));
                    return true;
                }
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    pet.Backpack.RemoveItem(carried);
                    _world.PlaceItemWithDecay(carried, pet.Position);
                }
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "drop all":
                if (pet.Backpack == null || pet.Backpack.Contents.Count == 0)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetCarrynothing));
                    return true;
                }
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    pet.Backpack.RemoveItem(carried);
                    _world.PlaceItemWithDecay(carried, pet.Position);
                }
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "equip":
                if (pet.Backpack == null)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                    return true;
                }
                bool equippedAny = false;
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    Layer layer = ResolveWearableLayer(carried);
                    if (layer == Layer.None || pet.GetEquippedItem(layer) != null)
                        continue;
                    pet.Backpack.RemoveItem(carried);
                    pet.Equip(carried, layer);
                    equippedAny = true;
                }
                NpcSpeech(pet, ServerMessages.Get(equippedAny ? Msg.NpcPetSuccess : Msg.NpcPetFailure));
                return true;

            case "status":
                if (pet.TryGetTag("HIRE_DAYS_LEFT", out string? days))
                    NpcSpeech(pet, ServerMessages.GetFormatted(Msg.NpcPetDaysLeft, days ?? "0"));
                else
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetEmployed));
                return true;

            case "cash":
            {
                // Source-X NPC_VendorGetChkVerb PC_CASH: only an owned vendor's
                // real earnings are dispensed to the owner (restock never tops
                // up an owned purse, so this cannot mint gold).
                if (!SphereNet.Game.Trade.VendorEngine.IsVendorLike(pet))
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetConfused));
                    return true;
                }
                int dispensed = SphereNet.Game.Trade.VendorEngine.DispenseVendorGold(pet, _character);
                NpcSpeech(pet, dispensed > 0
                    ? $"Here is thy gold: {dispensed}"
                    : "I have no gold for thee.");
                return true;
            }

            case "bought":
            case "samples":
            case "stock":
                // Source-X opens the vendor's owner-managed BOUGHT/SAMPLES/STOCK
                // container. SphereNet's vendor stock is template-driven (virtual,
                // rebuilt on restock), not an owner-managed inventory, so there is
                // nothing safe to hand out — report honestly instead of a bogus cursor.
                NpcSpeech(pet, SphereNet.Game.Trade.VendorEngine.IsVendorLike(pet)
                    ? "I manage my own stock."
                    : ServerMessages.Get(Msg.NpcPetConfused));
                return true;

            case "attack":
            case "kill":
            case "guard":
            case "follow":
            case "go":
            case "friend":
            case "unfriend":
            case "transfer":
            case "release":
            case "price":
                EmitPetTargetPrompt(pet, verb);
                return true;

            default:
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetConfused));
                return false;
        }
    }

    /// <summary>
    /// Source-X verbs that need a target open the cursor with the matching
    /// DEFMSG_NPC_PET_TARG_* prompt. The follow-up click is wired into
    /// ApplyPetTarget().
    /// </summary>
    private void EmitPetTargetPrompt(Character pet, string verb)
    {
        string promptKey = verb switch
        {
            "attack" or "kill" => Msg.NpcPetTargAtt,
            "guard"            => Msg.NpcPetTargGuard,
            "follow"           => Msg.NpcPetTargFollow,
            "friend"           => Msg.NpcPetTargFriend,
            "unfriend"         => Msg.NpcPetTargUnfriend,
            "transfer"         => Msg.NpcPetTargTransfer,
            "go"               => Msg.NpcPetTargGo,
            "price"            => Msg.NpcPetSetprice,
            _                  => Msg.NpcPetSuccess,
        };
        SysMessage(ServerMessages.Get(promptKey));
        SetPendingTarget(
            (serial, x, y, z, gfx) => ApplyPetTarget(pet, verb, new Serial(serial), x, y, z),
            cursorType: verb == "go" ? (byte)1 : (byte)0);
    }

    private void EmitPetTargetPrompt(IReadOnlyList<Character> pets, string verb)
    {
        if (pets.Count == 0)
            return;

        string promptKey = verb switch
        {
            "attack" or "kill" => Msg.NpcPetTargAtt,
            "guard" => Msg.NpcPetTargGuard,
            "follow" => Msg.NpcPetTargFollow,
            "friend" => Msg.NpcPetTargFriend,
            "unfriend" => Msg.NpcPetTargUnfriend,
            "transfer" => Msg.NpcPetTargTransfer,
            "go" => Msg.NpcPetTargGo,
            "price" => Msg.NpcPetSetprice,
            _ => Msg.NpcPetSuccess,
        };

        var petUids = pets.Select(p => p.Uid).ToList();
        SysMessage(ServerMessages.Get(promptKey));
        SetPendingTarget((serial, x, y, z, gfx) =>
            {
                foreach (var petUid in petUids)
                {
                    var pet = _world.FindChar(petUid);
                    if (pet == null || pet.IsDeleted || pet.IsDead || _character == null ||
                        !pet.CanAcceptPetCommandFrom(_character))
                    {
                        continue;
                    }

                    ApplyPetTarget(pet, verb, new Serial(serial), x, y, z);
                }
            },
            cursorType: verb == "go" ? (byte)1 : (byte)0);
    }

    /// <summary>Resolve a target picked after EmitPetTargetPrompt and apply the verb.</summary>
    private void ApplyPetTarget(Character pet, string verb, Serial uid, short x, short y, sbyte z)
    {
        if (_character == null) return;
        if (!pet.CanAcceptPetCommandFrom(_character))
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return;
        }

        var obj = uid.IsValid ? _world.FindObject(uid) : null;

        switch (verb)
        {
            case "attack":
            case "kill":
                if (obj is Character victim && victim != pet &&
                    !victim.IsDead && !victim.IsStatFlag(StatFlag.Invul) &&
                    !victim.IsStatFlag(StatFlag.Ridden) &&
                    victim != _character && victim.Uid != pet.NpcMaster)
                {
                    // Remember the mode to fall back to once the target dies,
                    // so the pet returns to Guard/Follow instead of trailing the
                    // master (ModernUO DoOrderNone behavior).
                    if (pet.PetAIMode != PetAIMode.Attack)
                        pet.SetTag("PREV_PET_MODE", ((int)pet.PetAIMode).ToString());
                    pet.SetTag("ATTACK_TARGET", victim.Uid.Value.ToString());
                    pet.FightTarget = victim.Uid;
                    pet.PetAIMode = PetAIMode.Attack;
                    OnWakeNpc?.Invoke(pet);
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "guard":
                if (obj is Character guarded)
                {
                    pet.SetTag("GUARD_TARGET", guarded.Uid.Value.ToString());
                    pet.PetAIMode = PetAIMode.Guard;
                    SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargGuardSuccess, pet.Name));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "follow":
                if (obj is Character followee)
                {
                    pet.SetTag("FOLLOW_TARGET", followee.Uid.Value.ToString());
                    pet.PetAIMode = PetAIMode.Follow;
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "friend":
                if (obj is Character friend && friend.IsPlayer)
                {
                    if (pet.IsSummoned)
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargFriendSummoned));
                    }
                    else if (pet.IsFriendOf(friend.Uid))
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargFriendAlready));
                    else
                    {
                        pet.AddFriend(friend);
                        SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess1, friend.Name));
                        if (friend != _character)
                            SendToChar?.Invoke(friend.Uid, new PacketSpeechUnicodeOut(
                                0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System",
                                ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess2, pet.Name)));
                    }
                }
                break;

            case "unfriend":
                if (obj is Character unfriend && pet.IsFriendOf(unfriend.Uid))
                {
                    pet.RemoveFriend(unfriend);
                    SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargUnfriendSuccess1, unfriend.Name));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetTargUnfriendNotfriend));
                break;

            case "transfer":
                if (obj is Character newOwner && newOwner.IsPlayer)
                {
                    if (pet.IsSummoned)
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargTransferSummoned));
                    }
                    else if (pet.TryAssignOwnership(newOwner, newOwner, summoned: false, enforceFollowerCap: true))
                    {
                        pet.PetAIMode = PetAIMode.Follow;
                        SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess2, newOwner.Name));
                    }
                    else
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                    }
                }
                break;

            case "release":
                if (obj is Character releaseOwner && pet.HasOwner(releaseOwner.Uid))
                {
                    pet.ClearOwnership(clearFriends: true);
                    pet.PetAIMode = PetAIMode.Stay;
                    pet.RemoveTag("ATTACK_TARGET");
                    pet.RemoveTag("GUARD_TARGET");
                    pet.RemoveTag("FOLLOW_TARGET");
                    pet.RemoveTag("GO_TARGET");
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "go":
                // Remember the order state so the pet resumes it on arrival
                // (Source-X NPCACT_GOTO → Act_Idle re-evaluation).
                if (!pet.TryGetTag("PREV_PET_MODE", out _))
                    pet.SetTag("PREV_PET_MODE", ((int)pet.PetAIMode).ToString());
                pet.SetTag("GO_TARGET", $"{x},{y},{z},{_character.MapIndex}");
                pet.PetAIMode = PetAIMode.Come;
                SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                break;

            case "price":
                if (obj is Item priced)
                {
                    priced.SetTag("PRICE", priced.Price > 0 ? priced.Price.ToString() : "1");
                    SendInputPromptGump(priced, "PRICE", 9);
                }
                break;
        }
    }

    private Layer ResolveWearableLayer(Item item)
    {
        var itemDef = DefinitionLoader.GetItemDef(item.BaseId);
        Layer layer = itemDef?.Layer ?? Layer.None;
        if (layer == Layer.None && _world.MapData != null)
        {
            var tile = _world.MapData.GetItemTileData(item.BaseId);
            if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 &&
                tile.Quality > 0 && tile.Quality <= (byte)Layer.Horse)
            {
                layer = (Layer)tile.Quality;
            }
        }
        return layer;
    }

    private IEnumerable<Character> CollectCommandablePets(string? namePrefix = null)
    {
        if (_character == null)
            return Enumerable.Empty<Character>();

        return _world.GetCharsInRange(_character.Position, 12)
            .Where(p =>
                !p.IsPlayer &&
                !p.IsDead &&
                !p.IsDeleted &&
                !p.IsStatFlag(StatFlag.Ridden) &&
                p.CanAcceptPetCommandFrom(_character) &&
                (string.IsNullOrEmpty(namePrefix) ||
                 p.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)));
    }

    internal void HandleVendorInteraction(Character vendor)
    {
        if (_character == null) return;

        // Build a buy/sell gump for the vendor
        var gump = new GumpBuilder(_character.Uid.Value, vendor.Uid.Value, 400, 300);
        gump.AddResizePic(0, 0, 5054, 400, 300);
        gump.AddText(30, 20, 0, vendor.GetName());
        gump.AddText(30, 50, 0, "How may I help you?");
        gump.AddButton(30, 100, 4005, 4007, 1);  // Buy
        gump.AddText(70, 100, 0, "Buy");
        gump.AddButton(30, 130, 4005, 4007, 2);  // Sell
        gump.AddText(70, 130, 0, "Sell");
        gump.AddButton(150, 250, 4017, 4019, 0); // Cancel

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (buttonId == 1)
                SendVendorBuyList(vendor);
            else if (buttonId == 2)
                SendVendorSellList(vendor);
        });
    }

    /// <summary>
    /// Source-X CClient::Cmd_VendorBuy parity. Public entry used when the
    /// player triggers buy via speech ("vendor buy", "buy") or by clicking
    /// the buy gump button. Wraps the private packet-formatting helper so
    /// callers outside this client (e.g. NPC speech dispatch in Program.cs)
    /// don't need to poke private members.
    /// </summary>
    public void OpenVendorBuy(Character vendor) => SendVendorBuyList(vendor);

    /// <summary>
    /// Source-X CClient::Cmd_VendorSell parity. Public entry used when the
    /// player triggers sell via speech or via the vendor gump button.
    /// </summary>
    public void OpenVendorSell(Character vendor) => SendVendorSellList(vendor);

    /// <summary>Send the vendor's buy list (items available for purchase) to the client.</summary>
    private void SendVendorBuyList(Character vendor)
    {
        if (_character == null) return;

        // Auto-restock if needed (TAG.VENDORINV path — used by GM-set
        // inventory definitions).
        if (VendorEngine.NeedsRestock(vendor))
            VendorEngine.RestockVendor(vendor);

        // Source-X parity: vendors restock from their @NPCRestock
        // trigger (SELL=VENDOR_S_*, BUY=VENDOR_B_*) when their stock
        // pack is empty. The spawn-time hook fires this on freshly
        // spawned NPCs, but vendors that were loaded from a prior
        // world save never went through that path. Re-fire on demand
        // so legacy persisted vendors get a stock list as soon as a
        // player tries to buy from them.
        // Vendor's stock lives on LAYER_VENDOR_STOCK (26). ClassicUO's
        // BuyList handler hard-rejects any other layer (Backpack = 21
        // is silently dropped), so we MUST source / reference the
        // dedicated vendor stock container.
        var stockContainer = vendor.GetEquippedItem(Layer.VendorStock);
        if (stockContainer == null ||
            !_world.GetContainerContents(stockContainer.Uid).Any())
        {
            // Rebuild the virtual stock from the persisted SELL template
            // (the stock items themselves are not saved). Covers vendors
            // loaded from a prior world save and those drained to empty.
            vendor.RebuildVendorStock();
            stockContainer = vendor.GetEquippedItem(Layer.VendorStock);

            // Only fall back to the @NPCRestock script trigger when the
            // persisted-tag rebuild produced nothing. Firing both spawned the
            // SELL list TWICE, so every row showed up doubled in the buy gump.
            bool rebuilt = stockContainer != null &&
                _world.GetContainerContents(stockContainer.Uid).Any();
            if (!rebuilt)
            {
                _triggerDispatcher?.FireCharTrigger(vendor,
                    SphereNet.Core.Enums.CharTrigger.NPCRestock,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = _character });
                // Refresh after restock — the trigger may have created it.
                stockContainer = vendor.GetEquippedItem(Layer.VendorStock);
            }
        }

        // Collect vendor inventory items (items in vendor's "sell" container / buy pack)
        var vendorItems = GetVendorBuyInventory(vendor);
        if (vendorItems.Count == 0 || stockContainer == null)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_no_goods"));
            return;
        }

        // Source-X / RunUO order (CClient::addVendorBuy):
        //   1) 0x2E equip the vendor stock container at LAYER_VENDOR_STOCK
        //      (=ClassicUO Layer.ShopBuyRestock 0x1A) so the client knows
        //      the entity exists.
        //   2) 0x3C container contents — every item that the buy list will
        //      reference. The client uses these entries to look up
        //      itemId/hue/amount when drawing each row of the buy window;
        //      without it the rows are blank.
        //   3) 0x74 vendor buy list — prices + descriptions. ClassicUO's
        //      BuyList(0x74) handler ONLY decorates the items with prices
        //      and display names; it does NOT push them into the
        //      ShopGump's display list. (See ShopGump.Update —
        //      `if (_shopItems.Count == 0) Dispose()` will close the
        //      gump after one frame if nothing was added.)
        //   4) 0x24 OpenContainer with gumpId=0x0030 + VENDOR MOBILE serial
        //      — THIS is what actually opens and populates the buy gump.
        //      The client's OpenContainer handler iterates
        //      vendor.FindItemByLayer(Layer.ShopBuyRestock..ShopBuy) and
        //      calls ShopGump.AddItem for every child item. Skipping this
        //      step is exactly why our buy menu used to "vanish" — the
        //      gump did spawn briefly and then auto-disposed because
        //      `_shopItems` stayed empty.
        var buyPack = stockContainer;
        uint buyContainerSerial = buyPack.Uid.Value;

        // (0) PRE-SYNC the vendor stock container as a worn item.
        //     Equipping at LAYER_VENDOR_STOCK (26 == ClassicUO
        //     Layer.ShopBuyRestock 0x1A) is mandatory: ClassicUO's
        //     BuyList(0x74) handler explicitly checks
        //     `container.Layer == Layer.ShopBuyRestock || == Layer.ShopBuy`
        //     and silently bails out for any other layer (including
        //     Backpack = 0x15).
        _netState.Send(new PacketWornItem(
            buyPack.Uid.Value, buyPack.BaseId, (byte)Layer.VendorStock,
            vendor.Uid.Value, buyPack.Hue.Value));

        // (0b) ALSO equip a container at LAYER_VENDOR_EXTRA (27 ==
        //      ClassicUO Layer.ShopBuy 0x1B). ClassicUO's OpenContainer
        //      handler for gump 0x0030 unconditionally iterates BOTH
        //      ShopBuyRestock and ShopBuy layers and calls `item.Items`
        //      on each — without a NULL-check. If the second layer is
        //      empty, `vendor.FindItemByLayer(Layer.ShopBuy)` returns
        //      null and the client CRASHES with NullReferenceException
        //      the moment we send our 0x24 to open the buy gump.
        //      Source-X NPCs always have both stock containers (LAYER
        //      26 + LAYER 27) for exactly this reason; we lazily mint
        //      the second one here so legacy / freshly-spawned vendors
        //      don't crash the client.
        var extraContainer = vendor.GetEquippedItem(Layer.VendorExtra);
        if (extraContainer == null)
        {
            extraContainer = _world.CreateItem();
            extraContainer.BaseId = 0x408D; // i_vendor_box (Source-X stock graphic)
            vendor.Equip(extraContainer, Layer.VendorExtra);
        }
        _netState.Send(new PacketWornItem(
            extraContainer.Uid.Value, extraContainer.BaseId, (byte)Layer.VendorExtra,
            vendor.Uid.Value, extraContainer.Hue.Value));

        var contentEntries = new List<PacketContainerContents.Entry>(vendorItems.Count);
        for (int i = 0; i < vendorItems.Count; i++)
        {
            var vi = vendorItems[i];
            // Cascade items inside the buy pack so the client can render
            // distinct rows. Five-wide grid matches Source-X / RunUO layout.
            short x = (short)(20 + (i % 5) * 30);
            short y = (short)(20 + (i / 5) * 20);
            contentEntries.Add(new PacketContainerContents.Entry(
                vi.Serial, vi.ItemId, 0, vi.Amount,
                x, y, buyContainerSerial, vi.Hue, (byte)i));
        }
        _netState.Send(new PacketContainerContents(contentEntries, _netState.IsClientPost6017));

        // ClassicUO's BuyList(0x74) walks the stock container's item list in
        // REVERSE: the 0x3C handler appends entries with PushToBack, and for
        // any container graphic other than 0x2AF8 the 0x74 loop starts at the
        // tail and steps Previous. The 0x74 entries must therefore be sent in
        // reverse of the 0x3C order (RunUO does the same) — otherwise every
        // row is decorated with another row's price and display name, and on
        // a count mismatch rows fall back to 0gp with no name.
        var buyListEntries = new List<VendorItem>(vendorItems);
        buyListEntries.Reverse();
        _netState.Send(new PacketVendorBuyList(buyContainerSerial, buyListEntries));

        // (4) Open the buy gump. ClassicUO's OpenContainer handler with
        //     gumpId=0x0030 walks vendor.FindItemByLayer(Layer.ShopBuyRestock
        //     .. Layer.ShopBuy), pulls every child item out, and calls
        //     ShopGump.AddItem. Without this packet, the gump that BuyList
        //     creates auto-disposes one frame later because its
        //     `_shopItems` dictionary is empty (see ShopGump.Update).
        //     Note: the serial here is the VENDOR MOBILE — not the
        //     container — because the handler does
        //     `World.Mobiles.Get(serial)`.
        _netState.Send(new PacketOpenContainer(vendor.Uid.Value, 0x0030,
            _netState.IsClientPost7090));
    }

    /// <summary>Send the sell list (items player can sell to this vendor) to the client.</summary>
    private void SendVendorSellList(Character vendor)
    {
        if (_character == null) return;

        var backpack = _character.Backpack;
        if (backpack == null)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nothing_buy"));
            return;
        }

        // Build list of items the vendor will buy from the player's backpack.
        // A vendor with a BUY list only lists items on it (Source-X
        // NPC_FindVendableItem); no list = buys anything (legacy behaviour).
        var buyFilter = SphereNet.Game.Trade.VendorEngine.GetVendorBuyFilter(vendor);
        var sellItems = new List<VendorItem>();
        foreach (var item in _world.GetContainerContents(backpack.Uid))
        {
            if (item.ItemType == ItemType.Gold) continue; // don't sell gold
            if (item.IsDeleted) continue;
            if (buyFilter != null && !buyFilter.Contains(item.BaseId)) continue;

            int price = GetVendorItemSellPrice(vendor, item);
            if (price <= 0) continue;

            sellItems.Add(new VendorItem
            {
                Serial = item.Uid.Value,
                ItemId = item.DispIdFull,
                Hue = item.Hue.Value,
                Amount = (ushort)item.Amount,
                Price = price,
                Name = item.GetName()
            });

            if (sellItems.Count >= 50) break; // limit
        }

        if (sellItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nothing_buy"));
            return;
        }

        _netState.Send(new PacketVendorSellList(vendor.Uid.Value, sellItems));
    }

    /// <summary>
    /// Build vendor buy inventory from vendor's TAG.SELL entries or equipped buy-pack items.
    /// In Sphere, vendor inventory is defined in CHARDEF with item entries.
    /// </summary>
    private List<VendorItem> GetVendorBuyInventory(Character vendor)
    {
        var items = new List<VendorItem>();

        // Items live on LAYER_VENDOR_STOCK (Source-X parity). ClassicUO
        // BuyList(0x74) only accepts containers equipped at that layer
        // (or LAYER_VENDOR_EXTRA = 27) — Backpack-based stock is dropped.
        var vendorPack = vendor.GetEquippedItem(Layer.VendorStock)
                         ?? vendor.GetEquippedItem(Layer.VendorExtra);
        if (vendorPack != null)
        {
            foreach (var item in _world.GetContainerContents(vendorPack.Uid))
            {
                if (item.IsDeleted) continue;

                int price = GetVendorItemPrice(vendor, item);
                items.Add(new VendorItem
                {
                    Serial = item.Uid.Value,
                    ItemId = item.DispIdFull,
                    Hue = item.Hue.Value,
                    Amount = Math.Max((ushort)1, (ushort)item.Amount),
                    Price = price,
                    Name = item.GetName()
                });

                if (items.Count >= 50) break;
            }
        }

        return items;
    }

    private bool IsValidTeleportDest(Core.Types.Point3D dest)
    {
        if (dest.X < 0 || dest.Y < 0) return false;
        if (_world.GetSector(dest) == null) return false;
        var md = _world.MapData;
        if (md == null) return true;
        var (mapW, mapH) = md.GetMapSize(dest.Map);
        if (dest.X >= mapW || dest.Y >= mapH) return false;
        // Reject blocked destinations (wall/water/impassable) so the moongate
        // can't strand the traveller inside geometry.
        return md.IsPassable(dest.Map, dest.X, dest.Y, dest.Z);
    }

    // B4: map a structured placement failure to a specific player message instead of
    // the single generic "Cannot place here".
    private string PlacementFailureMessage(SphereNet.Game.Housing.PlacementFailure failure, bool isShip)
    {
        string kind = isShip ? "ship" : "house";
        return failure switch
        {
            SphereNet.Game.Housing.PlacementFailure.PlayerLimitReached =>
                $"You already own the maximum number of {kind}s.",
            SphereNet.Game.Housing.PlacementFailure.AccountLimitReached =>
                $"This account already owns the maximum number of {kind}s.",
            SphereNet.Game.Housing.PlacementFailure.MultiDefinitionMissing =>
                "That deed's structure is not defined on this shard.",
            SphereNet.Game.Housing.PlacementFailure.OutOfMap =>
                "That location is off the edge of the map.",
            SphereNet.Game.Housing.PlacementFailure.LocationBlocked => isShip
                ? "A ship must be placed on open water, clear of obstructions."
                : "The location is blocked — the ground must be clear and flat.",
            SphereNet.Game.Housing.PlacementFailure.ScriptVeto =>
                $"You cannot place a {kind} here.",
            _ => isShip ? "Cannot place ship here." : ServerMessages.Get("house_cant_place"),
        };
    }

    private bool TryResolveDeedMulti(Item deed, out ushort multiId, out bool isShip)
    {
        isShip = false;
        multiId = 0;
        if (deed.TryGetTag("SHIP_MULTI_BASEID", out string? shipBase) &&
            TryParseDeedMultiId(shipBase, out ushort shipId, allowZero: true))
        {
            isShip = true;
            multiId = shipId;
            return true;
        }
        if (deed.TryGetTag("HOUSE_MULTI_BASEID", out string? houseBase) &&
            TryParseDeedMultiId(houseBase, out ushort houseId))
        {
            multiId = houseId;
            return true;
        }

        // Ship/house deeds reference their multi by defname (itemdef MORE=m_small_ship_n).
        // ItemDefHelper copies that as a raw "MORE" tag rather than the More1 property,
        // and a small-ship multi legitimately resolves to id 0 — so a numeric More1>0
        // test misses it entirely. Resolve the defname directly; id 0 is valid.
        foreach (string moreKey in MultiDeedTagKeys)
        {
            if (deed.TryGetTag(moreKey, out string? moreDef) && !string.IsNullOrWhiteSpace(moreDef))
            {
                int resolved = Item.ResolveMultiDefId?.Invoke(moreDef.Trim()) ?? -1;
                if (resolved >= 0)
                {
                    multiId = (ushort)resolved;
                    isShip = deed.BaseId == 0x14F1 ||
                        DefinitionLoader.GetItemDef((ushort)resolved)?.Type == ItemType.Ship;
                    return true;
                }
            }
        }

        ushort targetId = deed.More1 is > 0 and <= ushort.MaxValue
            ? (ushort)deed.More1
            : (ushort)0;
        // Source-X ITEMID_MULTI: a multi defname evaluates to 0x4000 + raw
        // index, so a deed whose @Create ran MORE=m_small_ship_n carries
        // More1=0x4000 for the raw-index-0 small ship. Strip the base back to
        // the multi.mul index the placement engines use.
        bool wasMultiBased = targetId is >= 0x4000 and < 0x8000;
        if (wasMultiBased)
            targetId = (ushort)(targetId - 0x4000);
        if (targetId == 0 && !wasMultiBased && deed.BaseId is not (0x14EF or 0x14F0 or 0x14F1) &&
            _housingEngine?.MultiDefs.Get(deed.BaseId) != null)
            targetId = deed.BaseId;
        if (targetId == 0 && !wasMultiBased) return false;

        isShip = deed.BaseId == 0x14F1 ||
            _housingEngine?.MultiDefs.Get(targetId)?.MultiTypeName
                .Equals("t_ship", StringComparison.OrdinalIgnoreCase) == true ||
            DefinitionLoader.GetItemDef(targetId)?.Type == ItemType.Ship;
        multiId = targetId;
        return true;
    }

    private static readonly string[] MultiDeedTagKeys = { "MORE1_DEFNAME", "MORE" };

    // allowZero: an explicit SHIP_MULTI_BASEID tag legitimately carries id 0 (small
    // ship north's raw multi index), so a dry-dock-generated redeed must accept it.
    // The ambiguous More1/BaseId fallback keeps rejecting 0 ("unset").
    private static bool TryParseDeedMultiId(string? text, out ushort id, bool allowZero = false)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out id)
            : ushort.TryParse(value, out id);
        return parsed && (allowZero || id != 0);
    }

    private void RestoreRedeededMultiUuid(Item deed, Item placedMulti, string tagName)
    {
        if (!deed.TryGetTag(tagName, out string? uuidText) || !Guid.TryParse(uuidText, out Guid uuid))
            return;
        Guid oldUuid = placedMulti.Uuid;
        placedMulti.Uuid = uuid;
        if (!_world.TryReIndexUuid(placedMulti, oldUuid, out _))
            placedMulti.Uuid = oldUuid;
    }

    // ==================== Crafting Gump ====================
}
