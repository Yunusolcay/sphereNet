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
    private bool UsePortcullis(Item gate) => _client.UsePortcullis(gate);
    private void FollowItemLinks(Item start) => _client.FollowItemLinks(start);
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
        // A parked creature - carrying a rider, stabled or shrunk - is out of its
        // sector and cannot be seen, but it stays in the world table at its last
        // position, so a double-click carrying its old uid still arrived here. The
        // engine refuses a second relationship on its own; this closes the door the
        // stale click came through.
        if (target.IsStatFlag(StatFlag.Ridden))
            return false;

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

    /// <summary>Source-X CanSee for an item, as the double-click path applies it.
    /// The book, map and board packet handlers gate on the same rule.</summary>
    internal bool CanSeeItem(Item item) => CanSeeItemForDoubleClick(item, out _);

    /// <summary>Source-X CanTouch: reach, not just sight.</summary>
    internal bool CanTouchItem(Item? item) => CanReachTargetItem(item);

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
                // A shrine resurrects THROUGH the spell: Source-X calls
                // OnSpellEffect(SPELL_Resurrection, ...) with the shrine as the source
                // (CClientUse.cpp:327), so @SpellEffect can refuse it before anything
                // happens (CCharSpell.cpp:3712). SphereNet went straight to the
                // resurrection, so only @Resurrect was ever consulted.
                if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellEffect,
                        new TriggerArgs
                        {
                            CharSrc = _character,
                            ItemSrc = item,
                            O1 = item,
                            N1 = (int)SpellType.Resurrection,
                        }) == TriggerResult.True)
                    return;

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
            {
                // Which side of the rail the user is on decides this, and the answer is
                // the ship's REGION, not whether they happen to stand on the plank
                // itself (IT_SHIP_PLANK, CCharUse.cpp:1810). Comparing coordinates sent
                // a passenger trying to shut the hatch walking onto it instead, and let
                // anyone standing on a LOCKED plank close it without the key.
                var shipEngine = Item.ResolveShipEngine?.Invoke();
                var ship = shipEngine?.GetShip(item.Link) ?? shipEngine?.FindShipAt(item.Position);
                bool aboard = ship != null && shipEngine?.FindShipAt(_character.Position) == ship;

                if (aboard)
                {
                    // Closing a plank whose side is locked needs the key, as opening it
                    // does.
                    if (item.More2 == (uint)ItemType.ShipSideLocked &&
                        _character.PrivLevel < PrivLevel.GM &&
                        FindBackpackKeyFor(item) == null)
                    {
                        SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                        SysMessage(ServerMessages.Get(Msg.LockContNoKey));
                        break;
                    }
                    item.ClosePlank();
                    break;
                }

                if (ship != null && _character.PrivLevel < PrivLevel.GM && !ship.CanBoard(_character.Uid))
                {
                    SysMessage(ServerMessages.Get(Msg.TillerNotyourship));
                    break;
                }
                _world.MoveCharacter(_character,
                    new Point3D(item.X, item.Y, (sbyte)(item.Z + 3), item.MapIndex));
                // Stepping aboard is a teleport, and a teleport reveals: Source-X runs
                // the plank boarding through Spell_Teleport, which ends in Reveal
                // (CCharUse.cpp:1827 -> CCharSpell.cpp:232 -> CCharAct.cpp:3491). A
                // hidden boarder used to stay hidden.
                _character.ClearStatFlag(StatFlag.Hidden);
                _character.ClearStatFlag(StatFlag.Invisible);
                SendSelfRedraw();
                break;
            }

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
                if (_character.Position.GetDistanceTo(item.Position) <= 3)
                {
                    // Through the shared damage path, not straight into the hit points:
                    // the reference springs a trap with OnTakeDamage(dmg, nullptr,
                    // DAMAGE_HIT_BLUNT|DAMAGE_GENERAL) (CCharUse.cpp:1753), which is
                    // what carries @GetHit and the resistances with it. Writing Hits
                    // directly meant a script could neither veto the damage nor change
                    // it.
                    CombatEngine.ApplyScriptDamage(_character, trapDmg,
                        DamageType.HitBlunt | DamageType.General);
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
                // One meal, one path: EatEngine carries the reference's @Eat
                // contract - ARGN1 is a STAT LIMIT starting at zero rather than the
                // hunger restored, the gains ride in LOCAL.Hits / Mana / Stam / Food
                // with the item as the object argument, and all of them are read back
                // (CCharAct.cpp:3456-3476). The old call passed N1=5, prepared no
                // locals and then applied a flat five regardless, so a script that
                // wrote those values changed nothing. RETURN 1 skips the gains but
                // still costs the food, as Use_EatQty consumes either way (:913).
                EatOneUnit(item);
                break;

            // Source-X routes t_grain/t_grass through Use_Eat and t_water_wash
            // through Use_Drink (CCharUse.cpp). These are almost always fixed
            // sources — water troughs/basins, decorative grass and hay — so the
            // hunger benefit is given but the item is NEVER deleted; only a
            // movable stack sitting in a container is decremented a unit. This
            // keeps a placed trough/tile intact (it was previously a dead
            // "you can't think of a way to use that" default).
            // Grain and grass ARE food in the reference - they go through Use_Eat like
            // anything else (CCharUse.cpp:1844), which asks whether the eater may move
            // the item and then consumes what was eaten, last unit included. Treating
            // them as inexhaustible fixtures let a single ear of grain feed a player
            // forever; the move rule is what actually protects a placed trough.
            case ItemType.Grain:
            case ItemType.Grass:
                if (!ItemMoveRules.CanMove(_character, item, out _))
                {
                    SysMessage(ServerMessages.Get(Msg.FoodCantmove));
                    break;
                }
                EatOneUnit(item);
                break;

            case ItemType.WaterWash:
                // Water is DRUNK in the reference (Use_Drink), which is a different
                // contract; left on the old path until that is modelled.
                SphereNet.Game.NPCs.EatEngine.Eat(_character, item, _triggerDispatcher, 1);
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

            // Source-X CClientUse.cpp:412 - a bloody bandage asks for a target and
            // is cleaned by using it on water.
            case ItemType.BandageBlood:
                SysMessage(ServerMessages.Get("target_promt"));
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => UseBloodyBandage(item, serial, x, y));
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
                    // A shorn sheep is answered too - the reference tells the player
                    // to wait rather than silently doing nothing (CClientTarg.cpp:1870).
                    if (targetObj is Character sheep && sheep.BodyId is 0x00CF or 0x00DF)
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
                    if (targetObj is Item seedSource &&
                        seedSource.ItemType is ItemType.Fruit or ItemType.ReagentRaw)
                    {
                        CutSeedFrom(seedSource);
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
                    // A destroyed web is simply gone: the reference's IT_WEB damage
                    // branch calls Delete() and creates nothing (CItem.cpp:5886). The
                    // "silk" this used to leave behind came from a stale comment in
                    // Use_Item_Web, and the graphic it used (0x0DF8) is wool, not silk.
                    _world.RemoveItem(item);
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
                {
                    // A board sets its pieces out before it opens (Game_Create,
                    // CItemContainer.cpp:1123) - SphereNet opened an empty board, so a
                    // new one could never be played on.
                    SetUpGameBoard(item);
                    SendOpenContainer(item);
                }
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

                // Source-X follows the item's LINK chain after the use itself
                // (Use_Item, CCharUse.cpp:1962) - a lever exists to work whatever it
                // is wired to, and only the graphic was flipping.
                FollowItemLinks(item);
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
                if (!DrinkBooze(item))
                    break;
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
                TrainOnDummy(item);
                break;
            case ItemType.TrainPickpocket:
                TrainOnPickpocketDip(item);
                break;
            case ItemType.ArcheryButte:
                // Standing right against the butte collects what is stuck in it,
                // BEFORE any thought of shooting (Use_Train_ArcheryButte,
                // CCharUse.cpp:453). SphereNet went straight to the skill, so ammunition
                // the butte had swallowed - from a script or a legacy save - could never
                // be pulled back out.
                if (GatherButteAmmo(item))
                    break;
                RouteSkillTarget(SkillType.Archery, item.Uid);
                break;

            // ---- kindling / bedroll / campfire ----
            case ItemType.Kindling:
                RouteSkillTarget(SkillType.Camping, item.Uid);
                break;
            case ItemType.Bedroll:
                UseBedroll(item);
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
                UseBeeHive(item);
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
                SetPendingItemTarget(item, (serial, x, y, z, gfx) => FillPitcher(item, serial, x, y));
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
                // Bound to the crystal, so the shared target path re-checks that the
                // SOURCE still exists, is still where it was and still answers
                // @TargOn_Item (CClientTarg.cpp:1683). The generic cursor skipped all
                // three, so a crystal that had changed hands mid-cursor was still
                // linked by its old holder.
                SetPendingItemTarget(item, (serial, _, _, _, _) =>
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
                }, cursorType: 0);
                break;

            // ---- portcullis ----
            case ItemType.PortLocked:
                // Source-X refuses a locked gate to anyone but a GM unless the use
                // arrived through a LINK (CCharUse.cpp:1771); it then falls through
                // to the ordinary portcullis move.
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemusePortLocked));
                    break;
                }
                goto case ItemType.Portculis;

            case ItemType.Portculis:
                // A vertical gate MOVES between two heights; it does not swap art
                // like a hinged door (Use_Portculis, CItem.cpp:4583).
                UsePortcullis(item);
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
        ushort growId = ResolvePlantId(def?.TData2 ?? 0, def?.TData2Name);
        ushort fruitId = ResolvePlantId(def?.TData3 ?? 0, def?.TData3Name);
        // MORE2 is SphereNet's per-instance fruit override, which the growth-timer
        // drop already honours (Source-X m_itCrop.m_ridFruitOverride).
        ushort fruitOverride = (ushort)Math.Clamp(item.More2, 0u, ushort.MaxValue);
        int amount = 1;

        // @ResourceTest comes FIRST and may rewrite the stage and the fruit:
        // ARGN1 = the growth id, ARGN2 = the fruit id, ARGN3 = the per-instance fruit
        // override (Plant_Use, CItemPlant.cpp:38). RETURN 1 abandons the harvest.
        if (_triggerDispatcher != null)
        {
            var test = new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = item,
                N1 = growId,
                N2 = fruitId,
                N3 = fruitOverride,
            };
            if (_triggerDispatcher.FireItemTrigger(item, ItemTrigger.ResourceTest, test) == TriggerResult.True)
                return;
            growId = ClampToId(test.N1);
            fruitId = ClampToId(test.N2);
            fruitOverride = ClampToId(test.N3);
        }

        // A stage that still has somewhere to grow is not ripe - and an unripe plant
        // yields nothing AND is not reset (:52). SphereNet looked only at whether a
        // fruit was defined, so a crop whose TDATA2 and TDATA3 are both set could be
        // reaped in mid-growth.
        if (growId != 0)
        {
            SysMessage("That is not ripe yet.");
            return;
        }

        if (fruitOverride != 0)
            fruitId = fruitOverride;
        if (fruitId == 0)
        {
            SysMessage("There is nothing to harvest yet.");
            return;
        }

        var fruit = _world.CreateItem();
        fruit.BaseId = fruitId;

        // @ResourceGather then carries the amount and the produce itself: ARGN1 = the
        // amount, ARGO = the fruit (:73). RETURN 1 destroys the produce and leaves the
        // plant standing - no crop reset. (The reference's HALFBAKED branch, which
        // drops the produce at the reaper's feet, has no equivalent return value in
        // SphereNet's interpreter and is left for that gap to be closed first.)
        if (_triggerDispatcher != null)
        {
            var gather = new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = fruit,
                O1 = fruit,
                N1 = amount,
            };
            var answer = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.ResourceGather, gather);
            amount = gather.N1 > 0 ? (int)gather.N1 : 1;
            if (answer == TriggerResult.True)
            {
                _world.RemoveItem(fruit);
                return;
            }
        }

        fruit.Amount = (ushort)Math.Clamp(amount, 1, ushort.MaxValue);
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

    /// <summary>A plant stage/fruit id from an ITEMDEF, numeric or by defname.</summary>
    private static ushort ResolvePlantId(uint tdata, string? tdataName)
    {
        if (tdata != 0) return (ushort)tdata;
        if (!string.IsNullOrEmpty(tdataName) && Item.ResolveDefName != null)
            return Item.ResolveDefName(tdataName);
        return 0;
    }

    private static ushort ClampToId(long value) =>
        value is > 0 and <= ushort.MaxValue ? (ushort)value : (ushort)0;

    /// <summary>Whether a seed can go into the ground here. Source-X looks for an
    /// IT_DIRT dynamic item, static or terrain tile at the spot
    /// (Use_Seed -> IsItemTypeNear, CWorldMap.cpp:663).</summary>
    private bool HasSoilAt(short x, short y)
    {
        if (_character == null) return false;
        byte map = _character.MapIndex;
        var spot = new Point3D(x, y, _character.Z, map);

        foreach (var there in _world.GetItemsInRange(spot, 0))
        {
            if (there.ItemType == ItemType.Dirt)
                return true;
        }

        var md = _world.MapData;
        if (md == null) return false;

        foreach (var st in md.GetStatics(map, x, y))
        {
            if (DefinitionLoader.GetItemDef(st.TileId)?.Type == ItemType.Dirt)
                return true;
        }

        // The reference reads the terrain's own type from the pack's tile-type table;
        // SphereNet classifies the land tile by its tiledata instead (the same source
        // its P.TYPE property uses).
        return SphereNet.Game.Objects.ObjBase.ClassifyTerrainType(
            md.GetLandTileData(md.GetTerrainTile(map, x, y).TileId)) == "t_dirt";
    }

    /// <summary>Take honey - or a sting - from a hive.
    ///
    /// Source-X keeps the hive's stock in MORE1 and spends it: an empty hive gives
    /// nothing, a full one rolls honey, beeswax or a sting, and only a product costs
    /// a unit. Either way the hive goes quiet for 15 minutes, and its own tick
    /// refills it up to five (CCharUse.cpp:1692; CItem.cpp:6380). SphereNet rolled a
    /// flat 60% honey with no stock and no timer at all, so a hive was an endless
    /// supply.</summary>
    private void UseBeeHive(Item hive)
    {
        if (_character == null) return;

        ushort made = 0;
        if (hive.More1 == 0)
        {
            SysMessage("The hive is empty.");
        }
        else
        {
            made = Random.Shared.Next(3) switch
            {
                1 => 0x09EC,    // ITEMID_JAR_HONEY
                2 => 0x1423,    // ITEMID_BEE_WAX
                _ => (ushort)0, // stung
            };
        }

        if (made != 0)
        {
            var product = _world.CreateItem();
            product.BaseId = made;
            PlaceItemInPack(_character, product);
            hive.More1 -= 1;
            SysMessage("You gather from the hive.");
        }
        else if (hive.More1 != 0)
        {
            SysMessage("You are stung by angry bees!");
            SphereNet.Game.Combat.CombatEngine.ApplyScriptDamage(
                _character, Random.Shared.Next(5),
                SphereNet.Game.Combat.DamageType.Poison | SphereNet.Game.Combat.DamageType.General);
        }

        hive.SetTimeout(Environment.TickCount64 + Item.BeeHiveRefillMs);
    }

    /// <summary>Fill an empty pitcher from a water source (Source-X
    /// CChar::Use_Item on IT_PITCHER_EMPTY).</summary>
    private void FillPitcher(Item pitcher, uint targetSerial, short x, short y)
    {
        if (_character == null) return;
        switch (ResolveWaterTarget(targetSerial, x, y))
        {
            case WaterTarget.OutOfReach:
                SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
                return;
            case WaterTarget.NotWater:
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

        // Source-X Use_Seed asks for soil before it spends anything, and a staff
        // member is the exception (CCharUse.cpp:1488). SphereNet planted on bare
        // rock, water or a floor.
        if (_character.PrivLevel < PrivLevel.GM && !HasSoilAt(here.X, here.Y))
        {
            SysMessage("You need to plant that in soil.");
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

        // What is already growing there decides the outcome: a tree or foliage
        // refuses the planting outright, while an existing crop is REPLACED rather
        // than stacked on top of (:1503). SphereNet piled a second crop onto the
        // same tile and left trees standing.
        var standing = _world.GetItemsInRange(new Point3D(x, y, z, _character.MapIndex), 0).ToArray();
        foreach (var there in standing)
        {
            if (there.ItemType is ItemType.Tree or ItemType.Foliage)
            {
                SysMessage("There is already a tree here.");
                return;
            }
        }
        foreach (var there in standing)
        {
            if (there.ItemType == ItemType.Crops)
                _world.RemoveItem(there);
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
    internal void OpenMapGump(Item item)
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
        // replaying ours, plot mode off. addMapMode sets the SERVER's plot mode from
        // the same value it sends (CClientMsg.cpp:2542) - leaving the stored flag on
        // while telling the client it is off made the next toggle answer "off" again,
        // so a map that had been edited before needed two clicks to start editing.
        item.RemoveTag("PLOTMODE");
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

    /// <summary>Whether this key opens that lock.
    ///
    /// Source-X compares LOCK CODES rather than demanding the key name the target
    /// object itself (Use_Key -> IsKeyLockFit, CItem.cpp:4278): a house key carries
    /// the multi's code, which every door of the house shares through its link. The
    /// active key path asked only for TAG.LINK == the target's own uid, so the game's
    /// own house key opened nothing - while the pack search below already knew the
    /// rule. One resolution now serves both.</summary>
    private static bool KeyFits(Item key, Item locked)
    {
        uint code = key.Link.IsValid ? key.Link.Value : 0;
        if (code == 0 && key.TryGetTag("LINK", out string? lk))
            uint.TryParse(lk, out code);
        if (code == 0) return false;

        if (code == locked.Uid.Value) return true;
        return locked.Link.IsValid && code == locked.Link.Value;
    }

    private Item? FindBackpackKeyFor(Item locked)
    {
        if (_character?.Backpack == null) return null;
        foreach (var it in EnumerateContents(_character.Backpack, 0))
        {
            if (it.ItemType is not (ItemType.Key or ItemType.Keyring)) continue;
            if (KeyFits(it, locked))
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

        int oreQty = Math.Max(1, (int)ore.Amount);
        ushort ingotId = ResolveSmeltIngotId(ore);
        int perOre = 1;

        // Source-X @Smelt arguments (Skill_Mining_Smelt, CCharSkill.cpp:1138):
        // ARGN1 = the smelter's Mining skill, ARGN2 = how many kinds of resource the
        // ore yields, ARGN3 = skip the minimum-skill requirement, and the produce
        // itself in LOCAL.resource.0.ID / .amount - all of it read back afterwards.
        // SphereNet passed the ore COUNT as ARGN1, nothing else, and threw the args
        // away, so a script could veto a smelt but never steer it.
        int miningSkill = _character.GetSkill(SkillType.Mining);
        bool skipSkillReq = false;
        if (_triggerDispatcher != null)
        {
            var locals = new SphereNet.Scripting.Variables.VarMap();
            locals.SetInt("resource.0.ID", ingotId);
            locals.SetInt("resource.0.amount", perOre);
            var args = new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = ore,
                O1 = forge,
                N1 = miningSkill,
                N2 = 1,             // an ore yields exactly one kind of resource
                N3 = 0,
                S1 = ServerMessages.Get(Msg.MiningSmelt),
                Locals = locals,
            };
            if (_triggerDispatcher.FireItemTrigger(ore, ItemTrigger.Smelt, args) == TriggerResult.True)
                return;

            miningSkill = args.N1;
            skipSkillReq = args.N3 != 0;
            if (long.TryParse(locals.Get("resource.0.ID"), out long scriptedId) &&
                scriptedId is > 0 and <= ushort.MaxValue)
                ingotId = (ushort)scriptedId;
            if (long.TryParse(locals.Get("resource.0.amount"), out long scriptedQty) && scriptedQty > 0)
                perOre = (int)Math.Min(scriptedQty, ushort.MaxValue);
        }

        if (!skipSkillReq && !SkillEngine.UseQuick(_character, SkillType.Mining, 30))
        {
            // A failed smelt costs part of the pile, not all of it: the reference
            // loses rand(amount/2)+1 (CCharSkill.cpp:1247). SphereNet deleted the
            // whole stack, so one unlucky roll burned ten ore.
            int lost = Random.Shared.Next(oreQty / 2) + 1;
            ConsumeOreAmount(ore, lost);
            SysMessage(ServerMessages.GetFormatted(Msg.MiningNothing, ore.GetName()));
            return;
        }

        var oreHue = ore.Hue;
        int amount = oreQty * Math.Max(1, perOre);
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

        // @Create belongs to the item that was just made, BEFORE it is handed over
        // and possibly merged into a pile that was already there: Source-X builds the
        // ingot with CreateScript and only bounces it afterwards (CCharSkill.cpp:1260
        // / :1284). Firing it on the merged result re-ran the creation script over the
        // player's existing ingots - a callback that recoloured the new ingots
        // recoloured the old ones with them.
        _triggerDispatcher?.FireItemTrigger(ingot, ItemTrigger.Create,
            new TriggerArgs { CharSrc = _character, ItemSrc = ingot });
        if (ingot.IsDeleted)
            return;

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
                return;
            }
        }

        _world.PlaceItemWithDecay(ingot, _character.Position);
    }

    /// <summary>Resolve the ingot id an ore smelts into.
    ///
    /// Source-X reads it from the ore definition's TDATA1 (m_ttOre.m_idIngot,
    /// CItemBase.h:145; Skill_Mining_Smelt, CCharSkill.cpp:1150), so a custom ore
    /// yields the ingot its own definition names. SphereNet knew only about the local
    /// TAG.SMELT_TO override and turned everything else into iron, carrying just the
    /// hue across. The explicit tag still wins - packs may already rely on it - and
    /// the native definition is the fallback ahead of plain iron.</summary>
    private static ushort ResolveSmeltIngotId(Item ore)
    {
        var def = DefinitionLoader.GetItemDef(ore.BaseId);

        string? raw = null;
        if (ore.TryGetTag("SMELT_TO", out string? itemTag) && !string.IsNullOrWhiteSpace(itemTag))
            raw = itemTag;
        else if (def != null && def.TagDefs.Has("SMELT_TO"))
            raw = def.TagDefs.Get("SMELT_TO");

        if (!string.IsNullOrWhiteSpace(raw))
        {
            raw = raw.Trim();
            bool ok = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort id)
                : ushort.TryParse(raw, out id);
            if (ok && id != 0)
                return id;
        }

        ushort native = ResolvePlantId(def?.TData1 ?? 0, def?.TData1Name);
        return native != 0 ? native : (ushort)0x1BF2;
    }

    /// <summary>Take part of a pile of ore, telling the client what is left.</summary>
    private void ConsumeOreAmount(Item ore, int lost)
    {
        if (lost >= ore.Amount)
        {
            ConsumeOreStack(ore);
            return;
        }

        ore.Amount -= (ushort)lost;
        if (ore.ContainedIn.IsValid)
            _netState.Send(new PacketContainerItem(
                ore.Uid.Value, ore.DispIdFull, 0, ore.Amount, ore.X, ore.Y,
                ore.ContainedIn.Value, ore.Hue, _netState.IsClientPost6017));
        else
            SendWorldItem(ore);
    }

    private void ConsumeOreStack(Item ore)
    {
        if (ore.ContainedIn.IsValid)
            _netState.Send(new PacketDeleteObject(ore.Uid.Value));
        else
            BroadcastDeleteObject(ore.Uid.Value);

        _world.RemoveItem(ore);
    }

    /// <summary>Source-X spins for two seconds (SetAnim, CClientTarg.cpp:2029).</summary>
    private const long SpinWheelBusyMs = 2000;

    private const ushort CleanBandageId = 0x0E21;   // ITEMID_BANDAGES1
    private const ushort PlainLeatherId = 0x1067;   // ITEMID_LEATHER_1

    /// <summary>Scissors: cloth and clothing become bandages, hides become leather.
    ///
    /// Source-X CUTS the target - it creates the OUTPUT item, carries the hue and
    /// the count over and deletes the input (IT_SCISSORS, CClientTarg.cpp:2110).
    /// SphereNet only rewrote the type field, so cloth stayed cloth-shaped with a
    /// bolt's type on it and no bandage was ever produced. Bloody bandages are not
    /// part of this branch at all: they are washed in water (see UseBloodyBandage).
    /// A cloth bolt needs ConvertBolttoCloth, which reads the RESOURCES definition
    /// - left alone rather than guessed at.</summary>
    private void HandleScissorsTarget(Item scissors, Serial target)
    {
        if (_character == null) return;
        var obj = target.IsValid ? _world.FindObject(target) as Item : null;
        if (obj == null || !CanReachTargetItem(obj)) { SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); return; }
        // A locked-down / Move_Never fixture (a placed cloth/hide decoration) must
        // not be cut or type-converted by scissors.
        if (!ItemMoveRules.CanMove(_character, obj, out _)) { SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); return; }

        ushort outId = CleanBandageId;
        int outQty;
        string message;
        switch (obj.ItemType)
        {
            case ItemType.Cloth:
                outQty = Math.Max(1, (int)obj.Amount);
                message = "You cut the cloth into bandages.";
                break;
            case ItemType.Clothing:
                // Worth its weight in bandages, as the reference measures it.
                outQty = obj.Weight / Item.WeightUnits;
                message = "You cut the clothing into bandages.";
                break;
            case ItemType.Hide:
                outId = ResolveHideOutput(obj);
                outQty = Math.Max(1, (int)obj.Amount);
                message = "You cut the hide into leather.";
                break;
            default:
                SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
                return;
        }

        if (outQty <= 0)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
            return;
        }

        var hue = obj.Hue;
        _world.RemoveItem(obj);

        var made = _world.CreateItem();
        made.BaseId = outId;
        made.Hue = hue;
        made.Amount = (ushort)Math.Min(outQty, ushort.MaxValue);
        PlaceItemInPack(_character, made);

        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketSound(0x0248, _character.X, _character.Y, _character.Z), 0);  // SOUND_SNIP
        SysMessage(message);
    }

    /// <summary>What a hide cuts into. Source-X reads the hide definition's TDATA1
    /// and falls back to plain leather (CClientTarg.cpp:2134), so a special hide
    /// keeps producing its own leather.</summary>
    private static ushort ResolveHideOutput(Item hide)
    {
        var def = DefinitionLoader.GetItemDef(hide.BaseId);
        uint tdata1 = def?.TData1 ?? 0;
        return tdata1 is > 0 and <= ushort.MaxValue ? (ushort)tdata1 : PlainLeatherId;
    }

    /// <summary>Bloody bandages are washed, not cut: they are used ON water
    /// (IT_BANDAGE_BLOOD, CClientTarg.cpp:2244). SphereNet had no such path and
    /// cleaned them with scissors instead, which the reference does not do - and it
    /// used the "clean these in water" message to announce success.</summary>
    private void UseBloodyBandage(Item bandages, uint targetSerial, short x, short y)
    {
        if (_character == null) return;

        switch (ResolveWaterTarget(targetSerial, x, y))
        {
            case WaterTarget.OutOfReach:
                SysMessage(ServerMessages.Get(Msg.ItemuseBandageReach));
                return;
            case WaterTarget.NotWater:
                SysMessage(ServerMessages.Get(Msg.ItemuseBandageClean));
                return;
        }

        // The whole pile comes back clean, as the reference's SetID does.
        bandages.BaseId = CleanBandageId;
        bandages.ItemType = ItemType.Bandage;
        Item.OnVisualUpdate?.Invoke(bandages);
        if (bandages.ContainedIn.IsValid)
            _netState.Send(new PacketContainerItem(
                bandages.Uid.Value, bandages.DispIdFull, 0, bandages.Amount,
                bandages.X, bandages.Y, bandages.ContainedIn.Value, bandages.Hue,
                _netState.IsClientPost6017));
    }

    private enum WaterTarget { Water, NotWater, OutOfReach }

    /// <summary>What the player actually pointed at. Source-X resolves this through
    /// CanTouchStatic (CCharStatus.cpp:1434), which answers with the TYPE of the
    /// dynamic item, the static or the terrain that was targeted - so a water trough
    /// standing on dry ground is water. SphereNet threw the target's serial away and
    /// looked only at the land tile beneath the coordinates.</summary>
    private WaterTarget ResolveWaterTarget(uint targetSerial, short x, short y)
    {
        if (_character == null) return WaterTarget.NotWater;

        if (_world.FindObject(new Serial(targetSerial)) is Item targetItem)
        {
            if (!CanReachTargetItem(targetItem))
                return WaterTarget.OutOfReach;
            return targetItem.ItemType is ItemType.Water or ItemType.WaterWash
                ? WaterTarget.Water
                : WaterTarget.NotWater;
        }

        var spot = new Point3D(x, y, _character.Z, _character.MapIndex);
        if (_character.PrivLevel < PrivLevel.GM &&
            _character.Position.GetDistanceTo(spot) > 3)
            return WaterTarget.OutOfReach;
        return IsWaterSpot(x, y) ? WaterTarget.Water : WaterTarget.NotWater;
    }

    /// <summary>Water under a targeted tile - the land itself, or a static laid over
    /// it (some shorelines and troughs are drawn as statics).</summary>
    private bool IsWaterSpot(short x, short y)
    {
        var md = _world.MapData;
        if (md == null) return false;
        if (_character == null) return false;

        byte map = _character.MapIndex;
        if (md.GetLandTileData(md.GetTerrainTile(map, x, y).TileId).IsWet)
            return true;
        foreach (var st in md.GetStatics(map, x, y))
        {
            if (md.GetItemTileData(st.TileId).IsWet)
                return true;
        }
        return false;
    }

    // Source-X bedroll graphics (uofiles_enums_itemid.h:115): open east-west and
    // north-south, the rolled-up one, and the two rolls that open a fixed way.
    private const ushort BedrollOpenEW = 0x0A55;
    private const ushort BedrollOpenNS = 0x0A56;
    private const ushort BedrollClosed = 0x0A57;
    private const ushort BedrollClosedNS = 0x0A58;
    private const ushort BedrollClosedEW = 0x0A59;

    /// <summary>Roll a bedroll out, or roll it back up (Use_BedRoll,
    /// CCharUse.cpp:1534). SphereNet said "you lay out the bedroll" and went straight
    /// to Camping without ever changing the item, so a bedroll never looked laid out.
    /// A rolled-up one has to be on the GROUND to open, which is what the reference
    /// tells the player instead.</summary>
    private void UseBedroll(Item bedroll)
    {
        if (_character == null) return;

        ushort id = bedroll.DispIdFull;
        if (id is BedrollOpenEW or BedrollOpenNS)
        {
            SetBedrollId(bedroll, BedrollClosed);
            return;
        }

        if (id is BedrollClosed or BedrollClosedNS or BedrollClosedEW)
        {
            if (bedroll.ContainedIn.IsValid)
            {
                SysMessage(ServerMessages.Get(Msg.ItemuseBedroll));
                return;
            }
            SetBedrollId(bedroll, id switch
            {
                BedrollClosedNS => BedrollOpenNS,
                BedrollClosedEW => BedrollOpenEW,
                _ => Random.Shared.Next(2) == 0 ? BedrollOpenEW : BedrollOpenNS,
            });
            return;
        }

        // A bedroll graphic the reference does not know still camps, which is what
        // SphereNet has always done here.
        SysMessage("You lay out the bedroll.");
        RouteSkillTarget(SkillType.Camping, bedroll.Uid);
    }

    private void SetBedrollId(Item bedroll, ushort id)
    {
        bedroll.BaseId = id;
        Item.OnVisualUpdate?.Invoke(bedroll);
        if (!bedroll.ContainedIn.IsValid)
            BroadcastNearby?.Invoke(bedroll.Position, UpdateRange,
                new PacketWorldItem(bedroll.Uid.Value, bedroll.DispIdFull, bedroll.Amount,
                    bedroll.X, bedroll.Y, bedroll.Z, bedroll.Hue), 0);
    }

    /// <summary>Drink something alcoholic.
    ///
    /// Source-X runs its own @Drink hook first - ARGN1 the effect delay, ARGN2 how much
    /// to consume, LOCAL.BottleId the empty it leaves, ARGO the drink - and reads them
    /// back, with RETURN 1 stopping the drink entirely (Use_Drink, CCharUse.cpp:1003).
    /// SphereNet fired @Eat instead, so nothing scripted for drinking could reach it.
    /// The drink then makes the drinker DRUNK: a Liquor effect that strengthens and
    /// lengthens if one is already running (:1031). SphereNet only fed the drinker and
    /// said "hic".
    ///
    /// Returns whether the bottle should be consumed.</summary>
    private bool DrinkBooze(Item drink)
    {
        if (_character == null) return false;

        var def = DefinitionLoader.GetItemDef(drink.BaseId);
        int delayTenths = (int)(def?.TData2 ?? 0);
        if (delayTenths <= 0) delayTenths = 1500;       // the reference's booze default
        int consume = 1;
        ushort bottleId = ResolvePlantId(def?.TData1 ?? 0, def?.TData1Name);

        if (_triggerDispatcher != null)
        {
            var locals = new SphereNet.Scripting.Variables.VarMap();
            locals.SetInt("BottleId", bottleId);
            var args = new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = drink,
                O1 = drink,
                N1 = delayTenths,
                N2 = consume,
                Locals = locals,
            };
            if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.Drink, args) == TriggerResult.True)
                return false;

            delayTenths = args.N1 > 0 ? args.N1 : 1;
            consume = args.N2;
        }

        // Getting drunk is the drink's own doing, hook or no hook: a Liquor effect
        // whose strength is rand(300)+10, which the reference lengthens and
        // strengthens when the drinker already has one running (:1031).
        _client.Spells?.ApplyDirectEffect(_character, _character,
            SphereNet.Core.Enums.SpellType.Liquor, Random.Shared.Next(300) + 10);

        _character.Food = (ushort)Math.Min(_character.Food + 2, 60);
        SysMessage("*hic!*");
        return consume > 0;
    }

    /// <summary>Eat one unit of something, and spend it only if it was actually
    /// eaten.
    ///
    /// Source-X leaves Use_EatQty before ConsumeAmount when the eater has no room left
    /// (CCharUse.cpp:889), so a full player loses nothing to a stray double-click.
    /// SphereNet ignored what the meal engine answered and took the unit regardless -
    /// deleting the last one outright.</summary>
    private void EatOneUnit(Item food)
    {
        if (_character == null) return;

        int eaten = SphereNet.Game.NPCs.EatEngine.Eat(_character, food, _triggerDispatcher, 1);
        if (eaten <= 0)
        {
            SysMessage(ServerMessages.Get(Msg.FoodFull6));
            return;
        }

        SysMessage(ServerMessages.Get("itemuse_eat_food"));
        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketAnimation(_character.Uid.Value, (ushort)AnimationType.Eat), 0);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketSound(0x003A, _character.X, _character.Y, _character.Z), 0);

        if (food.Amount > eaten)
        {
            food.Amount -= (ushort)eaten;
            if (food.ContainedIn.IsValid)
                _netState.Send(new PacketContainerItem(
                    food.Uid.Value, food.DispIdFull, 0, food.Amount, food.X, food.Y,
                    food.ContainedIn.Value, food.Hue, _netState.IsClientPost6017));
            else
                SendWorldItem(food);
            return;
        }

        if (_triggerDispatcher?.FireItemTrigger(food, ItemTrigger.Destroy,
                new TriggerArgs { CharSrc = _character, ItemSrc = food }) != TriggerResult.True)
            _world.RemoveItem(food);
    }

    /// <summary>Source-X SKILLPRACTICEMAX default: a training aid is only useful up
    /// to 30.0 skill (CServerConfig).</summary>
    private const int SkillPracticeMax = 300;

    // Source-X game pieces (uofiles_enums_itemid.h:937). GAME1 is the white set,
    // GAME2 the brown one.
    private const ushort Game1Checker = 0x3584, Game1Bishop = 0x3585, Game1Rook = 0x3586,
        Game1Queen = 0x3587, Game1Knight = 0x3588, Game1Pawn = 0x3589, Game1King = 0x358A,
        Game2Checker = 0x358B, Game2Bishop = 0x358C, Game2Rook = 0x358D,
        Game2Queen = 0x358E, Game2Knight = 0x358F, Game2Pawn = 0x3590, Game2King = 0x3591;

    /// <summary>Lay a game board out. Source-X Game_Create leaves a board that already
    /// has pieces alone and otherwise builds the set MORE1 asks for, at the container
    /// coordinates the client draws them on (CItemContainer.cpp:1123): chess, checkers,
    /// backgammon - and nothing for any other value.</summary>
    private void SetUpGameBoard(Item board)
    {
        if (board.Contents.Count > 0)
            return;     // a game already in progress

        switch (board.More1)
        {
            case 0: LayOutChess(board); break;
            case 1: LayOutCheckers(board); break;
            case 2: LayOutBackgammon(board); break;
        }
    }

    private void PlacePiece(Item board, ushort id, short x, short y)
    {
        var piece = _world.CreateItem();
        piece.BaseId = id;
        piece.ItemType = ItemType.GamePiece;
        if (!board.TryAddItem(piece))
        {
            _world.RemoveItem(piece);
            return;
        }
        piece.Position = new Point3D(x, y, 0, _character?.MapIndex ?? 0);
    }

    private void LayOutChess(Item board)
    {
        ushort[] pieces =
        [
            Game1Rook, Game1Knight, Game1Bishop, Game1Queen, Game1King, Game1Bishop, Game1Knight, Game1Rook,
            Game1Pawn, Game1Pawn, Game1Pawn, Game1Pawn, Game1Pawn, Game1Pawn, Game1Pawn, Game1Pawn,
            Game2Pawn, Game2Pawn, Game2Pawn, Game2Pawn, Game2Pawn, Game2Pawn, Game2Pawn, Game2Pawn,
            Game2Rook, Game2Knight, Game2Bishop, Game2Queen, Game2King, Game2Bishop, Game2Knight, Game2Rook,
        ];
        short[] rows = [5, 40, 160, 184];

        short x = 0, y = 0;
        for (int i = 0; i < pieces.Length; i++)
        {
            if ((i & 7) == 0) { x = 42; y = rows[i / 8]; }
            else x += 25;
            PlacePiece(board, pieces[i], x, y);
        }
    }

    private void LayOutCheckers(Item board)
    {
        short[] rows = [30, 55, 80, 155, 180, 205];
        short x = 0, y = 0;
        for (int i = 0; i < 24; i++)
        {
            if ((i & 3) == 0)
            {
                x = (short)(((i / 4) & 1) != 0 ? 67 : 42);
                y = rows[i / 4];
            }
            else x += 50;
            PlacePiece(board, i >= 12 ? Game1Checker : Game2Checker, x, y);
        }
    }

    private void LayOutBackgammon(Item board)
    {
        short[] rows =
        [
            8, 23, 38, 53, 68, 128, 143, 158, 173, 188, 8, 23, 158, 173, 188,
            128, 143, 158, 173, 188, 8, 23, 38, 53, 68, 173, 188, 8, 23, 38,
        ];
        short x = 0;
        for (int i = 0; i < 30; i++)
        {
            x = i switch
            {
                12 or 27 => 107,
                10 or 25 => 224,
                5 or 20 => 141,
                0 or 15 => 41,
                _ => x,
            };
            PlacePiece(board, i >= 15 ? Game1Checker : Game2Checker, x, rows[i]);
        }
    }

    /// <summary>Swing at a training dummy.
    ///
    /// Source-X wants the dummy on the ground within a tile, refuses a mounted or
    /// ranged-armed trainee and anyone past the practice cap, then swings: the dummy
    /// spins for three seconds, makes a noise and pays out experience in the weapon
    /// skill actually being used (Use_Train_Dummy, CCharUse.cpp:337). SphereNet played
    /// a sound and handed the dummy to the generic skill pipeline, which sets up no
    /// training at all - and picked the skill from whatever filled the two-handed layer
    /// rather than from the weapon.</summary>
    private void TrainOnDummy(Item dummy)
    {
        if (_character == null) return;

        if (dummy.ContainedIn.IsValid || _character.Position.GetDistanceTo(dummy.Position) > 1)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseTrainingdummyToofar));
            return;
        }
        if (_character.IsMounted)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseTrainingdummyMount));
            return;
        }

        var skill = ResolveWeaponSkill();
        if (skill is SkillType.Archery)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseTrainingdummyRanged));
            return;
        }
        if (_character.GetSkill(skill) > SkillPracticeMax)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseTrainingdummySkill));
            return;
        }

        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketAnimation(_character.Uid.Value, (ushort)AnimationType.AttackWeapon), 0);

        ushort[] sounds = [0x03A4, 0x03A6, 0x03A9, 0x03AE, 0x03B4, 0x03B6];
        BroadcastNearby?.Invoke(dummy.Position, UpdateRange,
            new PacketSound(sounds[Random.Shared.Next(sounds.Length)], dummy.X, dummy.Y, dummy.Z), 0);

        dummy.SetAnim((ushort)(dummy.DispIdFull + 1), 3000);
        SkillEngine.GainExperience(_character, skill, Random.Shared.Next(40));
    }

    /// <summary>The skill the wielded weapon actually trains (Source-X
    /// Fight_GetWeaponSkill): the weapon in either hand decides it, and bare hands
    /// mean wrestling.</summary>
    private SkillType ResolveWeaponSkill()
    {
        if (_character == null) return SkillType.Wrestling;
        var weapon = _character.GetEquippedItem(Layer.OneHanded)
                     ?? _character.GetEquippedItem(Layer.TwoHanded);
        if (weapon == null || !weapon.IsWeaponType) return SkillType.Wrestling;

        return weapon.ItemType switch
        {
            ItemType.WeaponSword or ItemType.WeaponAxe or ItemType.WeaponMaceSharp
                => SkillType.Swordsmanship,
            ItemType.WeaponFence => SkillType.Fencing,
            ItemType.WeaponMaceSmith or ItemType.WeaponMaceStaff or
            ItemType.WeaponMaceCrook or ItemType.WeaponMacePick => SkillType.MaceFighting,
            ItemType.WeaponBow or ItemType.WeaponXBow => SkillType.Archery,
            ItemType.WeaponThrowing => SkillType.Throwing,
            _ => SkillType.Wrestling,
        };
    }

    /// <summary>Practise stealing on the dip.
    ///
    /// The item is a TRAINING AID, not loot: Source-X asks for a ground item within one
    /// tile, refuses a mounted trainee and anyone already past the practice cap, then
    /// rolls the skill and leaves the dip exactly where it stands
    /// (Use_Train_PickPocketDip, CCharUse.cpp:397). SphereNet routed the double-click
    /// into the ordinary Stealing skill, which on success carried the dip itself into
    /// the thief's pack - a fixed one included.</summary>
    private void TrainOnPickpocketDip(Item dip)
    {
        if (_character == null) return;

        if (dip.ContainedIn.IsValid || _character.Position.GetDistanceTo(dip.Position) > 1)
        {
            SysMessage(ServerMessages.Get(Msg.ItemusePickpocketToofar));
            return;
        }
        if (_character.IsMounted)
        {
            SysMessage(ServerMessages.Get(Msg.ItemusePickpocketMount));
            return;
        }
        if (_character.GetSkill(SkillType.Stealing) > SkillPracticeMax)
        {
            SysMessage(ServerMessages.Get(Msg.ItemusePickpocketSkill));
            return;
        }

        BroadcastNearby?.Invoke(dip.Position, UpdateRange,
            new PacketSound(0x0057, dip.X, dip.Y, dip.Z), 0);   // SOUND_RUSTLE

        int difficulty = Random.Shared.Next(40);
        bool ok = SkillEngine.UseQuick(_character, SkillType.Stealing, difficulty);
        SysMessage(ServerMessages.Get(ok
            ? Msg.ItemusePickpocketSuccess
            : Msg.ItemusePickpocketFail));
        dip.SetAnim((ushort)(ok ? dip.DispIdFull : dip.DispIdFull + 1), 3000);
    }

    /// <summary>Take back the arrows and bolts a butte has collected. Reports whether
    /// there was anything to take (Use_Train_ArcheryButte, CCharUse.cpp:459): MORE1 is
    /// the ammunition kind, MORE2 how much, and both are cleared once it is handed
    /// over.</summary>
    private bool GatherButteAmmo(Item butte)
    {
        if (_character == null) return false;
        if (_character.Position.GetDistanceTo(butte.Position) >= 2) return false;
        if (butte.More2 == 0 || butte.More1 == 0) return false;

        var ammo = _world.CreateItem();
        ammo.BaseId = (ushort)Math.Clamp(butte.More1, 1u, ushort.MaxValue);
        ammo.Amount = (ushort)Math.Clamp(butte.More2, 1u, ushort.MaxValue);
        PlaceItemInPack(_character, ammo);

        butte.More1 = 0;
        butte.More2 = 0;
        SysMessage(ServerMessages.Get(Msg.ItemuseArchbutteGather));
        return true;
    }

    /// <summary>Source-X blade-on-corpse: carving through DeathEngine.CarveCorpse —
    /// the engine existed but no player input path reached it (audit #2).</summary>
    /// <summary>A fruit or a raw reagent cut open gives a seed: the reference swaps
    /// the graphic for the DEFAULTSEED one, retypes it IT_SEED and renames it
    /// "&lt;name&gt; seed" in place (CClientTarg.cpp:1939). SphereNet's blade dispatcher
    /// had no such branch, so the whole fruit-to-seed-to-plant chain started
    /// nowhere.</summary>
    private void CutSeedFrom(Item fruit)
    {
        if (_character == null) return;
        if (!CanConsumeTarget(fruit))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
            return;
        }

        ushort seedId = Item.ResolveDefName?.Invoke("DEFAULTSEED") ?? 0;
        if (seedId == 0)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
            return;
        }

        string was = fruit.GetName();
        fruit.BaseId = seedId;
        fruit.ItemType = ItemType.Seed;
        fruit.Name = $"{was} seed";
        Item.OnVisualUpdate?.Invoke(fruit);
        if (fruit.ContainedIn.IsValid)
            _netState.Send(new PacketContainerItem(
                fruit.Uid.Value, fruit.DispIdFull, 0, fruit.Amount, fruit.X, fruit.Y,
                fruit.ContainedIn.Value, fruit.Hue, _netState.IsClientPost6017));
    }

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
        if (sheep.IsDead) return;
        if (sheep.BodyId == 0x00DF)
        {
            SysMessage(ServerMessages.Get("itemuse_weapon_wwait"));
            return;
        }
        if (sheep.BodyId != 0x00CF) return;
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

        // The fleece grows back on a timer the sheep CARRIES: Source-X hangs a second
        // wool item on LAYER_FLAG_Wool with WOOLGROWTHTIME on it, and when that
        // expires the shorn body becomes a sheep again (CClientTarg.cpp:1862 ->
        // OnTickEquip, CCharAct.cpp:4067). SphereNet made no such record at all, so
        // nothing but a respawn ever un-sheared it.
        var regrow = _world.CreateItem();
        regrow.BaseId = 0x0DF8;
        regrow.ItemType = ItemType.EqMemoryObj;
        regrow.SetAttr(ObjAttributes.Newbie | ObjAttributes.Move_Never);
        sheep.Equip(regrow, Layer.FlagWool);
        regrow.SetTimeout(Environment.TickCount64 + Item.WoolGrowthMs);

        SysMessage("You shear the sheep and collect the wool.");
    }

    /// <summary>Source-X blade-on-dead-fish: fillet into raw fish steaks.</summary>
    private void FilletFish(Item fish)
    {
        if (_character == null) return;

        // Reaching a thing is not the same as being allowed to consume it: Source-X
        // asks CanUse(target, MOVE) here (IT_FISH, CClientTarg.cpp:1919), which brings
        // the move rules and the take-crime check with it. Without them a decorative
        // fixed fish vanished and another player's catch could be cut up out of their
        // own pack.
        if (!CanConsumeTarget(fish))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseFishUnable));
            return;
        }

        // The catch is CUT WHERE IT LIES - the reference changes the same item's
        // graphic, clears its hue and multiplies the amount by four. Making a new pile
        // in the pack moved someone else's fish into the cutter's own hands.
        fish.BaseId = 0x097A;   // ITEMID_FOOD_FISH_RAW
        fish.ItemType = ItemType.Food;
        fish.Hue = new Core.Types.Color(0);
        fish.Amount = (ushort)Math.Min(ushort.MaxValue, Math.Max(1, (int)fish.Amount) * 4);
        Item.OnVisualUpdate?.Invoke(fish);
        if (fish.ContainedIn.IsValid)
            _netState.Send(new PacketContainerItem(
                fish.Uid.Value, fish.DispIdFull, 0, fish.Amount, fish.X, fish.Y,
                fish.ContainedIn.Value, fish.Hue, _netState.IsClientPost6017));
        SysMessage("You cut the fish into raw fish steaks.");
    }

    /// <summary>Source-X CanUse(item, fMoveOrConsume: true) (CCharStatus.cpp:1736):
    /// reach, plus the move rules and the take-crime check that consuming implies.
    /// SphereNet's use paths asked only for reach, so a fixed or someone else's item
    /// could be spent.</summary>
    private bool CanConsumeTarget(Item target)
    {
        if (_character == null) return false;
        if (_character.PrivLevel >= PrivLevel.GM) return true;

        if (!CanReachTargetItem(target))
            return false;
        if (!ItemMoveRules.CanMove(_character, target, out _))
            return false;

        // Whatever is inside somebody else is theirs; the ground and my own pack are
        // fair game (the reference's IsTakeCrime for a container it does not own).
        var top = target.ResolveTopObject();
        return top is not Character owner || ReferenceEquals(owner, _character);
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

        // Both ends are checked: the muzzle has to be within reach, and the charge has
        // to be one this player may actually spend (Use_Cannon_Feed, CCharUse.cpp:301).
        // Neither was, so a cannon across the map took a charge out of a stranger's
        // pack.
        if (_character.PrivLevel < PrivLevel.GM && !CanReachTargetItem(cannon))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
            return;
        }
        if (!CanConsumeTarget(feed))
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

        // The wheel turns for two seconds and is not a spinning wheel while it does:
        // Source-X SetAnim(id+1, 2s) parks it in IT_ANIM_ACTIVE (CClientTarg.cpp:2029
        // -> CItem.cpp:4128), so a second batch cannot be fed to the same wheel until
        // it stops. SphereNet left the wheel idle and instantly reusable.
        wheel.SetAnim((ushort)(wheel.DispIdFull + 1), SpinWheelBusyMs);

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

        if (!KeyFits(key, obj)) { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNokey)); return; }

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
        // The vat wears the colour it will hand out - that is what the reference
        // reads when the vat is used (GetHue, CClientTarg.cpp:2331). A private tag
        // gave the vat two different colours and left the visible one inert.
        vat.Hue = dye.Hue;
        vat.RemoveTag("DYE_HUE");   // a legacy vat's stale copy must not outrank it
        Item.OnVisualUpdate?.Invoke(vat);
        SysMessage("You apply the dye to the vat.");
    }

    /// <summary>The colour a vat hands out. Its own hue is the authority; a vat
    /// saved before that was true is read from its old DYE_HUE tag only while it
    /// carries no hue of its own.</summary>
    private static ushort ResolveVatHue(Item vat)
    {
        if (vat.Hue.Value != 0)
            return vat.Hue.Value;
        return vat.TryGetTag("DYE_HUE", out string? hueText) &&
               ushort.TryParse(hueText, out ushort legacy) ? legacy : (ushort)0;
    }

    /// <summary>Whether this target may be dyed at all. Source-X requires the actor
    /// to own the top-level object and the item to be CAN_I_DYE or clothing, with a
    /// GM exception (CClientTarg.cpp:2302/2325). SphereNet asked only for reach, so
    /// undyeable gold, a stranger's goods and anything lying nearby took the
    /// colour.</summary>
    private bool CanDyeTarget(Item dest)
    {
        if (_character == null) return false;
        if (_character.PrivLevel >= PrivLevel.GM) return true;

        if (!ReferenceEquals(dest.ResolveTopObject(), _character))
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseDyeReach));
            return false;
        }

        var def = DefinitionLoader.GetItemDef(dest.BaseId);
        bool dyeable = dest.ItemType == ItemType.Clothing ||
                       def?.Dye == true ||
                       (def != null && (def.Can & Core.Enums.CanFlags.I_Dye) != 0);
        if (!dyeable)
        {
            SysMessage(ServerMessages.Get(Msg.ItemuseDyeFail));
            return false;
        }
        return true;
    }

    /// <summary>Apply a DyeVat hue to a target item.</summary>
    private void HandleDyeApply(Item vat, Serial target)
    {
        var dest = target.IsValid ? _world.FindObject(target) as Item : null;
        if (dest == null || !CanReachTargetItem(dest)) { SysMessage(ServerMessages.Get(Msg.ItemuseDyeReach)); return; }
        if (!CanDyeTarget(dest)) return;

        ushort hue = ResolveVatHue(vat);
        if (hue == 0) return;

        // Source-X SetHue seeds ARGN1 with the colour and ARGN2 with the sound, and
        // takes BOTH back from the script when it falls through (CObjBase.cpp:324).
        // The args object was thrown away here, so a script could veto the dye but
        // never change what colour it produced.
        var args = new TriggerArgs
        {
            CharSrc = _character,
            ItemSrc = dest,
            O1 = vat,
            N1 = hue,
            N2 = 0x23E,     // the reference's dye sound
        };
        if (_triggerDispatcher?.FireItemTrigger(dest, ItemTrigger.Dye, args) == TriggerResult.True)
            return;

        if (args.N1 is < 0 or > ushort.MaxValue)
            return;
        dest.Hue = new Core.Types.Color((ushort)args.N1);

        if (args.N2 > 0 && _character != null)
            BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                new PacketSound((ushort)args.N2, _character.X, _character.Y, _character.Z), 0);

        // Broadcast the recolour to every nearby client. The view-delta only
        // tracks GROUND items, so a worn/equipped item dyed this way would
        // otherwise stay its old colour on observers (and self) until a full
        // resync. OnVisualUpdate → SendItemVisualUpdate emits 0x2E for worn
        // items, 0x1A for ground, 0x25 for the owner's pack.
        Item.OnVisualUpdate?.Invoke(dest);
        SysMessage("The item changes color.");
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

    /// <summary>The only commands a pet FRIEND may give. Source-X opens exactly
    /// PC_FOLLOW, PC_STAY and PC_STOP to friends and sends every other verb to the
    /// default arm, which requires NPC_IsOwnedBy (CCharNPCPet.cpp:129-152). Being a
    /// friend was treated as full authority here, so a friend could make the pet drop
    /// its cargo or transfer the pet to themselves.
    ///
    /// Note that COME and FOLLOW ME are NOT in the reference's friend set: PC_COME and
    /// PC_FOLLOW_ME are separate commands (:38, :43) that fall to the owner-only
    /// arm.</summary>
    private static bool IsFriendPermittedPetVerb(string verb) =>
        verb is "follow" or "stay" or "stop";

    /// <summary>A new order supersedes whatever the pet was told last. Source-X starts
    /// a fresh NPC action per command - NPCACT_FOLLOW_TARG for come/follow me
    /// (CCharNPCPet.cpp:183), NPCACT_GOTO for go (:504) - so a pending GO cannot
    /// outlive the order that replaced it. SphereNet kept GO_TARGET in a tag the pet
    /// AI consults FIRST, so a Come after a Go changed the mode and then walked the
    /// pet on to the old spot anyway.
    ///
    /// The GO verb manages these itself: it re-points GO_TARGET and deliberately keeps
    /// the PREV_PET_MODE captured by the first GO, so the pet resumes what it was
    /// doing before the detour rather than resuming the detour.</summary>
    private static void SupersedePendingPetOrder(Character pet)
    {
        pet.RemoveTag("GO_TARGET");
        pet.RemoveTag("PREV_PET_MODE");
    }

    /// <summary>Source-X PC_*: target a single pet by name prefix.</summary>
    private bool DispatchNamedPet(string namePrefix, string verb)
    {
        if (_character == null) return false;
        var pet = CollectCommandablePets(namePrefix, verb).FirstOrDefault();
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
        var pets = CollectCommandablePets(null, verb).ToList();
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
        if (!pet.CanAcceptPetCommandFrom(_character, IsFriendPermittedPetVerb(verb)))
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
                SupersedePendingPetOrder(pet);
                pet.PetAIMode = PetAIMode.Follow;
                pet.FightTarget = Serial.Invalid; // an order calls the pet off its fight
                pet.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
                // @Follow (Source-X) — pet begins following its master. <src> = master.
                _triggerDispatcher?.FireCharTrigger(pet, CharTrigger.Follow,
                    new TriggerArgs { CharSrc = _character, O1 = _character });
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "come":
                SupersedePendingPetOrder(pet);
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
                SupersedePendingPetOrder(pet);
                pet.PetAIMode = PetAIMode.Stay;
                pet.FightTarget = Serial.Invalid; // an order calls the pet off its fight
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "guard me":
                SupersedePendingPetOrder(pet);
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
                DumpPetPack(pet);
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "drop all":
            {
                // Source-X PC_DROP_ALL is NOT the pack loop PC_DROP runs: it calls
                // DropAll (CCharNPCPet.cpp:255 -> CCharAct.cpp:564), which dumps the
                // pack and then hands the worn equipment to UnEquipAllItems (:592).
                // SphereNet repeated the drop branch verbatim, so an owner could never
                // get a weapon off a pet - and an empty pack ended the command before
                // the equipment was looked at at all.
                //
                // A conjured creature drops nothing whatsoever (CCharAct.cpp:567); its
                // gear leaves with it.
                if (pet.IsStatFlag(StatFlag.Conjured))
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                    return true;
                }

                bool droppedAny = DumpPetPack(pet);

                // The order matters: the pack is emptied FIRST and the equipment lands
                // in it afterwards. Stripping into the pack before the dump would put
                // the worn gear on the ground with everything else.
                droppedAny |= UnequipPetIntoPack(pet);

                NpcSpeech(pet, ServerMessages.Get(droppedAny
                    ? Msg.NpcPetSuccess
                    : Msg.NpcPetCarrynothing));
                return true;
            }

            case "equip":
            {
                if (pet.Backpack == null)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                    return true;
                }
                bool equippedAny = false;
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    if (TryPetEquip(pet, carried))
                        equippedAny = true;
                }
                NpcSpeech(pet, ServerMessages.Get(equippedAny ? Msg.NpcPetSuccess : Msg.NpcPetFailure));
                return true;
            }

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
                    // Re-checked at the click, not just when the cursor opened:
                    // ownership or friendship can be revoked while it is up.
                    if (pet == null || pet.IsDeleted || pet.IsDead || _character == null ||
                        !pet.CanAcceptPetCommandFrom(_character, IsFriendPermittedPetVerb(verb)))
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
        if (!pet.CanAcceptPetCommandFrom(_character, IsFriendPermittedPetVerb(verb)))
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
                    // Clear the previous order FIRST. Superseding drops
                    // PREV_PET_MODE along with the stale GO it belonged to, so writing
                    // the fallback before it destroyed the very value the attack path
                    // saves - the pet then fell back to Follow and trailed its master
                    // instead of returning to Guard or Stay.
                    SupersedePendingPetOrder(pet);

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
                    SupersedePendingPetOrder(pet);
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
                    SupersedePendingPetOrder(pet);
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

    /// <summary>Items a pet keeps rather than throwing on the ground.
    ///
    /// Source-X ContentsDump is handed ATTR_OWNED by both pet drop verbs and adds
    /// ATTR_NEWBIE / ATTR_MOVE_NEVER / ATTR_CURSED2 / ATTR_BLESSED2 to it
    /// (CContainer.cpp:502). SphereNet emptied the pack wholesale, so a hireling's
    /// own stock and an owner's blessed goods hit the dirt with the rest.</summary>
    private static bool StaysInPetPack(Item item) =>
        item.IsAttr(ObjAttributes.Owned) || item.IsAttr(ObjAttributes.Newbie) ||
        item.IsAttr(ObjAttributes.Move_Never) || item.IsAttr(ObjAttributes.Cursed2) ||
        item.IsAttr(ObjAttributes.Blessed2);

    /// <summary>Empty what the pet is carrying onto the ground at its feet.
    /// Reports whether anything actually left the pack.</summary>
    private bool DumpPetPack(Character pet)
    {
        var pack = pet.Backpack;
        if (pack == null)
            return false;

        bool dropped = false;
        foreach (var carried in pack.Contents.ToArray())
        {
            if (StaysInPetPack(carried))
                continue;
            pack.RemoveItem(carried);
            _world.PlaceItemWithDecay(carried, pet.Position);
            dropped = true;
        }
        return dropped;
    }

    /// <summary>Take the pet's worn equipment off into its own pack, as Source-X
    /// UnEquipAllItems does with a null destination (CCharAct.cpp:592/662).
    ///
    /// The reference walks the visible layers only - above LAYER_NONE up through
    /// LAYER_HORSE (CItemBase.cpp:548) - and explicitly leaves the pack, the mount,
    /// hair and beard where they are. Memories, spell effects and the vendor/bank
    /// containers live above that range and are never touched.</summary>
    private bool UnequipPetIntoPack(Character pet)
    {
        bool stripped = false;
        for (int raw = (int)Layer.OneHanded; raw <= (int)Layer.Horse; raw++)
        {
            var layer = (Layer)raw;
            if (layer is Layer.Hair or Layer.FacialHair or Layer.Pack or Layer.Horse)
                continue;

            var worn = pet.GetEquippedItem(layer);
            if (worn == null)
                continue;

            pet.Unequip(layer);
            var pack = pet.Backpack;
            if (pack == null || !pack.TryAddItem(worn))
            {
                worn.ContainedIn = Serial.Invalid;
                _world.PlaceItemWithDecay(worn, pet.Position);
            }
            stripped = true;
        }
        return stripped;
    }

    /// <summary>Whether the pet's OTHER hand already rules this one out.
    ///
    /// Source-X pairs the two hands inside CanEquipLayer (CCharStatus.cpp:410): a
    /// weapon taking HAND2 conflicts with whatever HAND1 holds, and a HAND1 equip
    /// conflicts with a WEAPON on HAND2 - never with a shield, which is why sword
    /// plus shield is a legal pair. The spoken command only ever looked at the
    /// item's own layer, so a pet ended up holding a sword and a two-handed bow at
    /// the same time.
    ///
    /// The scan skips the item rather than stripping the occupied hand: above
    /// CanEquipLayer, ItemEquipWeapon looks for a weapon at all only while neither
    /// hand holds one (CCharUse.cpp:2051), so the reference never displaces gear
    /// the owner did not ask to have taken off.</summary>
    private static bool OtherHandIsTaken(Character pet, Item item, Layer layer)
    {
        // What the reference means by a weapon here is CCPropsItemWeapon::CanSubscribe,
        // which a shield does not answer to. For the item still in the pack the
        // TWOHANDS flag counts as well, for a two-hander whose TYPE the pack leaves
        // unset; for the item already worn it does NOT - Item.IsTwoHanded reads the
        // layer an equipped item sits on, so a shield would look two-handed to it.
        if (layer == Layer.TwoHanded && (item.IsWeaponType || item.IsTwoHanded))
            return pet.GetEquippedItem(Layer.OneHanded) != null;
        if (layer == Layer.OneHanded)
            return pet.GetEquippedItem(Layer.TwoHanded) is { IsWeaponType: true };
        return false;
    }

    /// <summary>Wear one item out of a pet's pack the way Source-X ItemEquip does
    /// (CCharAct.cpp:3313): score the layer, let the script veto it, leave all but
    /// one piece of a stack behind, and run @Equip once it is actually worn.</summary>
    private bool TryPetEquip(Character pet, Item carried)
    {
        var pack = pet.Backpack;
        if (pack == null || carried.IsDeleted)
            return false;

        Layer layer = ResolveWearableLayer(carried);
        if (layer == Layer.None)
            return false;

        // Character.Equip promotes a two-hander off the one-handed layer some
        // tiledata gives it, so the slot to score is the one it will really take.
        if (layer == Layer.OneHanded && carried.IsTwoHanded)
            layer = Layer.TwoHanded;

        if (pet.GetEquippedItem(layer) != null || OtherHandIsTaken(pet, carried, layer))
            return false;

        // Source-X scores a candidate through CanEquipLayer with fTest, which turns
        // the strength requirement on for an NPC too (CCharNPCStatus.cpp:688 ->
        // CCharStatus.cpp:333/297). Character.Equip is the low-level placement and
        // enforces nothing, so the spoken command dressed a ten-strength pet in a
        // weapon needing eighty. A refused item stays in the pack.
        if (!pet.CanEquip(carried, layer, out _))
            return false;

        // @EquipTest, before anything is moved: RETURN 1 refuses the item and the
        // reference bounces it back into the NPC's pack, which is where it already
        // is here (CCharAct.cpp:3308). The callback may also have destroyed it or
        // taken it out of the pack, which the reference re-checks at :3331.
        if (_triggerDispatcher != null)
        {
            var test = _triggerDispatcher.FireItemTrigger(carried, ItemTrigger.EquipTest,
                new TriggerArgs { CharSrc = pet, ItemSrc = carried });
            if (test == TriggerResult.True)
                return false;
            if (carried.IsDeleted || carried.ContainedIn != pack.Uid)
                return false;
        }

        // A pile wears one piece and leaves the rest behind (UnStackSplit(1),
        // CCharAct.cpp:3337 -> CItem.cpp:1251). Without it a stack of five went onto
        // the layer whole. The worn piece keeps the original identity, as it does
        // there; the remainder is a full clone so tags, durability and attributes
        // survive the split.
        pack.RemoveItem(carried);
        if (carried.Amount > 1)
        {
            var remainder = _world.CreateItem();
            remainder.CopyStackInstanceStateFrom(carried);
            remainder.Amount = (ushort)(carried.Amount - 1);
            carried.Amount = 1;
            if (!pack.TryAddItem(remainder))
                _world.PlaceItemWithDecay(remainder, pet.Position);
        }

        if (!pet.Equip(carried, layer))
        {
            // Nowhere to be: put it back rather than leaving it parentless.
            if (!pack.TryAddItem(carried))
                _world.PlaceItemWithDecay(carried, pet.Position);
            return false;
        }

        _triggerDispatcher?.FireItemTrigger(carried, ItemTrigger.Equip,
            new TriggerArgs { CharSrc = pet, ItemSrc = carried });
        return true;
    }

    internal Layer ResolveWearableLayer(Item item)
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

    private IEnumerable<Character> CollectCommandablePets(string? namePrefix, string verb)
    {
        if (_character == null)
            return Enumerable.Empty<Character>();

        return _world.GetCharsInRange(_character.Position, 12)
            .Where(p =>
                !p.IsPlayer &&
                !p.IsDead &&
                !p.IsDeleted &&
                !p.IsStatFlag(StatFlag.Ridden) &&
                p.CanAcceptPetCommandFrom(_character, IsFriendPermittedPetVerb(verb)) &&
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
