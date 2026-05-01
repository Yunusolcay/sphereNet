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

    private void SendDrawObject(Character ch)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);

        _netState.Send(new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, ch.Hue, flags, noto,
            equipment
        ));
    }

    public void BeginTeleportTarget()
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingTeleTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginRemoveTarget()
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingRemoveTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>.XRESURRECT — opens a target cursor; the picked mobile (or
    /// the owner of the picked corpse) is resurrected via OnResurrectOther.</summary>
    public void BeginResurrectTarget()
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        ClearPendingTargetState();
        _pendingResurrectTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>.info without a UID — opens a target cursor; whatever
    /// the GM clicks on lands in <see cref="ShowInspectDialog"/>.</summary>
    public void BeginInspectTarget()
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        ClearPendingTargetState();
        _pendingInspectTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginAddTarget(string addToken)
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingAddToken = addToken.Trim();
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginShowTarget(string showArgs)
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingShowArgs = string.IsNullOrWhiteSpace(showArgs) ? "EVENTS" : showArgs.Trim();
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginEditTarget(string editArgs)
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingEditArgs = editArgs?.Trim() ?? "";
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>Source-X parity (CClient.cpp:921 <c>addTargetVerb</c>):
    /// stash an inner verb + arg pair, open a target cursor, and apply
    /// the verb to whatever the GM picks. Used by the generic X-prefix
    /// fallback (.xhits, .xcolor, .xinvul, ...).</summary>
    public void BeginXVerbTarget(string verb, string args)
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        if (string.IsNullOrEmpty(verb)) return;

        ClearPendingTargetState();
        _pendingXVerb = verb.Trim();
        _pendingXVerbArgs = args?.Trim() ?? "";
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>Source-X CV_NUKE / CV_NUKECHAR / CV_NUDGE: open a
    /// ground-target cursor and treat the picked tile as the centre of
    /// an axis-aligned area of half-extent <paramref name="range"/>.
    /// We deviate from Source-X (which prompts for two corner tiles —
    /// see CClient_functions.tbl 'NUKE'); a single pick + fixed range
    /// keeps the wire round-trip lean and is enough for GM cleanup.</summary>
    public void BeginAreaTarget(string verb, int range)
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        if (string.IsNullOrEmpty(verb)) return;

        ClearPendingTargetState();
        _pendingAreaVerb = verb.Trim().ToUpperInvariant();
        _pendingAreaRange = Math.Clamp(range, 1, 32);
        _targetCursorActive = true;
        // type=1 (ground allowed), so the GM can pick an empty tile.
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginControlTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingControlTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginDupeTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingDupeTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginHealTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingHealTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginKillTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingKillTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginBankTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingBankTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginSummonToTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingSummonToTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginMountTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingMountTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginSummonCageTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingSummonCageTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>Source-X CV_ANIM. Plays the given action on this client's
    /// own character so the GM can verify animation IDs visually.</summary>
    public void PlayOwnAnimation(ushort animId)
    {
        if (_character == null) return;
        var pkt = new PacketAnimation(_character.Uid.Value, animId);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, pkt, 0);
    }

    /// <summary>Convenience wrapper used by SpeechEngine event hookup —
    /// dismount the GM's character if currently mounted.</summary>
    public void UnmountSelf()
    {
        if (_character == null || _mountEngine == null) return;
        _mountEngine.Dismount(_character);
    }

    private void ClearPendingTargetState()
    {
        _pendingTeleTarget = false;
        _pendingAddToken = null;
        _pendingShowArgs = null;
        _pendingEditArgs = null;
        _pendingXVerb = null;
        _pendingXVerbArgs = "";
        _pendingAreaVerb = null;
        _pendingAreaRange = 0;
        _pendingControlTarget = false;
        _pendingDupeTarget = false;
        _pendingHealTarget = false;
        _pendingBankTarget = false;
        _pendingSummonToTarget = false;
        _pendingMountTarget = false;
        _pendingSummonCageTarget = false;
        _pendingRemoveTarget = false;
        _pendingResurrectTarget = false;
        _pendingInspectTarget = false;
        _pendingTargetFunction = null;
        _pendingTargetArgs = "";
        _pendingTargetAllowGround = false;
        _pendingTargetItemUid = Serial.Invalid;
        _pendingScriptNewItem = null;
        _lastScriptTargetPoint = null;
        _targetCursorActive = false;
    }

    /// <summary>Source-X Cmd_EditItem parity (CClientUse.cpp:577).
    /// If the target is a container (Item with contents, or Character with
    /// equipment) we display a 0x7C item-list menu so the GM can pick a
    /// child object to inspect.  Non-containers go straight to the prop
    /// dialog.</summary>
    public void ShowInspectDialog(uint uid, int requestedPage = 0)
    {
        if (_character == null) return;
        ObjBase? obj = _world.FindObject(new Serial(uid));
        if (obj == null)
        {
            SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{uid:X8}"));
            return;
        }

        var childItems = CollectContainerChildren(obj);
        if (childItems.Count == 0)
        {
            OpenInspectPropDialog(obj, requestedPage);
            return;
        }

        var entries = new List<MenuItemEntry>();
        var uids = new List<uint>();
        var mems = new List<Item?>();
        int max = Math.Min(childItems.Count, 254);
        for (int i = 0; i < max; i++)
        {
            var item = childItems[i];
            uids.Add(item.Uid.Value);
            ushort hue = 0;
            if (item.ItemType == Core.Enums.ItemType.EqMemoryObj)
            {
                var targetName = item.Link.IsValid ? (_world.FindObject(item.Link)?.Name ?? "?") : "?";
                entries.Add(new MenuItemEntry(item.BaseId, hue,
                    $"Memory: {targetName} [{item.GetMemoryTypes()}]"));
                mems.Add(item);
            }
            else
            {
                ushort rawHue = item.Hue;
                if (rawHue != 0)
                    hue = rawHue == 1 ? (ushort)0x7FF : (ushort)(rawHue - 1);
                entries.Add(new MenuItemEntry(item.BaseId, hue, item.Name));
                mems.Add(null);
            }
        }

        _pendingEditMenuUids = uids.ToArray();
        _pendingEditMenuMemories = mems.ToArray();
        _netState.Send(new PacketMenuDisplay(
            obj.Uid.Value, EditMenuId,
            $"Contents of {obj.Name}", entries));
    }

    public void HandleEditMenuChoice(ushort index)
    {
        if (_character == null) return;
        var uids = _pendingEditMenuUids;
        var mems = _pendingEditMenuMemories;
        _pendingEditMenuUids = null;
        _pendingEditMenuMemories = null;

        if (uids == null || index == 0 || index > uids.Length)
            return;

        uint picked = uids[index - 1];

        if (picked == 0 && mems != null && index - 1 < mems.Length)
        {
            var mem = mems[index - 1];
            if (mem != null)
            {
                var targetName = mem.Link.IsValid ? (_world.FindObject(mem.Link)?.Name ?? "?") : "?";
                SysMessage($"[Memory] Link=0x{mem.Link.Value:X8} ({targetName})");
                SysMessage($"  Types={mem.GetMemoryTypes()} Pos={mem.MoreP}");
                return;
            }
        }

        ShowInspectDialog(picked);
    }

    public void OpenInspectPropDialog(ObjBase obj, int requestedPage)
    {
        if (obj is Character)
            SendSkillList();

        string dialogId = obj is Item ? "d_itemprop1" : "d_charprop1";
        int page = Math.Max(0, requestedPage);
        if (OpenNamedDialog(dialogId, page, obj))
            return;

        SysMessage(ServerMessages.GetFormatted("gm_object_not_found", $"{obj.Uid.Value:X8}"));
    }

    private static List<Item> CollectContainerChildren(ObjBase obj)
    {
        var result = new List<Item>();
        if (obj is Item item && item.Contents.Count > 0)
        {
            foreach (var child in item.Contents)
                result.Add(child);
        }
        else if (obj is Character ch)
        {
            for (int i = 0; i < (int)Layer.Qty; i++)
            {
                var eq = ch.GetEquippedItem((Layer)i);
                if (eq != null)
                    result.Add(eq);
            }
            foreach (var mem in ch.Memories)
                result.Add(mem);
        }
        return result;
    }

    public void ShowTextDialog(string title, IReadOnlyList<string> lines)
    {
        if (_character == null)
            return;

        string combined = string.Join("\n", lines);
        var gump = new GumpBuilder(_character.Uid.Value, (uint)Math.Abs($"showdlg:{title}".GetHashCode()), 640, 420);
        gump.AddResizePic(0, 0, 5054, 640, 420);
        // Keep close button available by default for utility dialogs.
        gump.AddText(20, 15, 0, title);
        gump.AddHtmlGump(20, 45, 600, 180, EscapeHtml(combined).Replace("\n", "<br>"), true, true);
        // Text entry area allows easy select/copy by user.
        gump.AddText(20, 235, 0, "Copy-ready text:");
        gump.AddTextEntry(20, 260, 600, 120, 0, 1, combined);
        gump.AddButton(280, 390, 4005, 4007, 0);
        SendGump(gump);
    }

    private void SendUpdateMobile(Character ch)
    {
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);
        _netState.Send(new PacketMobileMoving(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z, (byte)ch.Direction,
            ch.Hue, flags, noto
        ));
    }

    private void SendUpdateMobileWithHue(Character ch, ushort hue)
    {
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);
        _netState.Send(new PacketMobileMoving(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z, (byte)ch.Direction,
            hue, flags, noto
        ));
    }

    private void SendUpdateMobileHidden(Character ch)
    {
        byte flags = (byte)(BuildMobileFlags(ch) | 0x80);
        byte noto = GetNotoriety(ch);
        _netState.Send(new PacketMobileMoving(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z, (byte)ch.Direction,
            ch.Hue, flags, noto
        ));
    }

    private void SendDrawObjectWithHue(Character ch, ushort hue)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);

        _netState.Send(new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, hue, flags, noto,
            equipment
        ));
    }

    private void SendDrawObjectHidden(Character ch)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = (byte)(BuildMobileFlags(ch) | 0x80);
        byte noto = GetNotoriety(ch);

        _netState.Send(new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, ch.Hue, flags, noto,
            equipment
        ));
    }

    private void SendWorldItem(Item item)
    {
        _netState.Send(new PacketWorldItem(
            item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, item.Hue
        ));
    }

    private void SendWorldItemWithHue(Item item, ushort hue)
    {
        _netState.Send(new PacketWorldItem(
            item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, hue
        ));
    }

    private void SendWorldItemAllShow(Item item)
    {
        _netState.Send(new PacketWorldItem(
            item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, item.Hue
        ));
    }

    /// <summary>Place a dragged item into the target character's backpack and
    /// send the client a 0x25 ContainerItem packet so it actually appears there.
    /// Without the packet the client only sees the previous 0x1D delete and
    /// treats the item as gone — classic "drop onto mobile = item vanishes"
    /// bug. If the backpack is somehow missing we recreate one so the item
    /// doesn't simply get lost.</summary>
    private void PlaceItemInPack(Character target, Item item)
    {
        var pack = target.Backpack;
        if (pack == null && target.IsPlayer)
        {
            EnsurePlayerBackpack(target);
            pack = target.Backpack;
        }
        if (pack == null)
        {
            // NPC without a pack: fall back to equip layer Pack or drop at feet.
            _world.PlaceItem(item, target.Position);
            return;
        }

        pack.AddItem(item);
        item.Position = new Point3D(0, 0, 0, target.MapIndex);

        // Owner-side visual: only clients that already have the pack "open"
        // need the 0x25 update; but Sphere/ServUO both send it unconditionally
        // because it's cheap and keeps drag preview consistent. Send to the
        // owner who initiated the drop.
        _netState.Send(new PacketContainerItem(
            item.Uid.Value, item.DispIdFull, 0,
            item.Amount, item.X, item.Y,
            pack.Uid.Value, item.Hue,
            _netState.IsClientPost6017));

        if (item.BaseId == 0x0EED && target == _character)
            SendCharacterStatus(_character);
    }

    private void SendOpenContainer(Item container)
    {
        // Source-X CClient::addContainerSetup parity: before opening
        // any container the client must already know about that
        // container as either a worn item (0x2E) or a world item
        // (0x1A). Otherwise the 0x24 OpenContainer is silently
        // dropped because the client can't resolve the serial.
        // Bank box / backpack open from a fresh login (or right after
        // we lazily-create the bank box) is the common case where this
        // pre-broadcast is missing.
        var parentChar = container.ContainedIn.IsValid
            ? _world.FindChar(container.ContainedIn)
            : null;
        if (parentChar != null)
        {
            byte layer = (byte)container.EquipLayer;
            _netState.Send(new PacketWornItem(
                container.Uid.Value, container.BaseId, layer,
                parentChar.Uid.Value, container.Hue.Value));
        }

        // Per-container gump selection (Source-X CItemBase::IsTypeContainer
        // returns m_ttContainer.m_idGump = TDATA2; ServUO does the equivalent
        // via Data/containers.cfg ItemID→GumpID lookup).
        //
        // Resolution order:
        //   1) ITEMDEF.TDATA2 if the script supplied one — script wins.
        //   2) Built-in fallback table for the well-known UO container
        //      graphics (matches ServUO's Data/containers.cfg shipped table).
        //   3) Bank-box layer fallback to 0x004A (silver bank chest, used
        //      with item ids 0xE7C / 0x9AB) — keeps GM-spawned bank boxes
        //      that have no itemdef render correctly.
        //   4) Generic bag fallback 0x003C.
        ushort gumpId = ResolveContainerGump(container);
        _netState.Send(new PacketOpenContainer(container.Uid.Value, gumpId, _netState.IsClientPost7090));

        foreach (var child in _world.GetContainerContents(container.Uid))
        {
            _netState.Send(new PacketContainerItem(
                child.Uid.Value, child.DispIdFull, 0,
                child.Amount, child.X, child.Y,
                container.Uid.Value, child.Hue,
                _netState.IsClientPost6017
            ));
        }
    }

    /// <summary>
    /// Resolve the container gump (0x24 second word) for a given container item.
    /// Mirrors Source-X CItemBase::IsTypeContainer (TDATA2 = m_idGump) and the
    /// ServUO Data/containers.cfg fallback table. Adding a new container ID to
    /// the built-in table or to ITEMDEF.TDATA2 in script is enough — no other
    /// code path needs to know about it.
    /// </summary>
    private static ushort ResolveContainerGump(Item container)
    {
        // 1) Script-supplied TDATA2 wins.
        var idef = Definitions.DefinitionLoader.GetItemDef(container.BaseId);
        if (idef != null && idef.TData2 != 0)
            return (ushort)(idef.TData2 & 0xFFFF);

        // 2) Built-in ItemID -> GumpID table. Mirrors the ServUO
        // Data/containers.cfg shipped table; covers the well-known UO
        // container graphics so vanilla content renders correctly even
        // when no scripted ITEMDEF is loaded.
        switch (container.BaseId)
        {
            // 0x4A — silver bank chest (BankBox)
            case 0xE7C:
            case 0x9AB:
                return 0x004A;
            // 0x3D — small wooden chest with iron bands
            case 0xE76:
            case 0x2256:
            case 0x2257:
                return 0x003D;
            // 0x3E — wooden box (no banding)
            case 0xE77:
            case 0xE7F:
                return 0x003E;
            // 0x3F — gold-banded chest
            case 0xE7A:
            case 0x24D5:
            case 0x24D6:
            case 0x24D9:
            case 0x24DA:
                return 0x003F;
            // 0x42 — pouch / standard backpack
            case 0xE40:
            case 0xE41:
                return 0x0042;
            // 0x43 — wooden chest with no bands
            case 0xE7D:
            case 0x9AA:
                return 0x0043;
            // 0x44 — large wood box
            case 0xE7E:
            case 0x9A9:
            case 0xE3C:
            case 0xE3D:
            case 0xE3E:
            case 0xE3F:
                return 0x0044;
            // 0x49 — small wooden chest gilt edges
            case 0xE42:
            case 0xE43:
                return 0x0049;
            // 0x4B — large metal chest
            case 0xE80:
            case 0x9A8:
                return 0x004B;
            default:
                break;
        }

        // 3) Layer-based bank-box fallback (covers GM-spawned bank boxes
        // whose itemId we don't recognize).
        if (container.EquipLayer == Layer.BankBox)
            return 0x004A;

        // 4) Generic bag.
        return 0x003C;
    }

    /// <summary>
    /// Open the player's bank box. Creates it if it doesn't exist.
    /// The bank box is a container item stored on the character at a special layer.
    /// </summary>
    public void OpenBankBox()
    {
        if (_character == null) return;

        // Look for existing bank box item on the character
        var bankBox = _character.GetEquippedItem(Layer.BankBox);
        if (bankBox == null)
        {
            // Create bank box
            bankBox = _world.CreateItem();
            bankBox.BaseId = 0x09AB; // bank box container graphic
            bankBox.ItemType = ItemType.Container;
            bankBox.Name = "Bank Box";
            _character.Equip(bankBox, Layer.BankBox);
        }

        SendOpenContainer(bankBox);
    }

    private readonly Dictionary<uint, long> _paperdollThrottle = [];

    public void SendPaperdoll(Character ch)
    {
        long now = Environment.TickCount64;
        if (_paperdollThrottle.TryGetValue(ch.Uid.Value, out long last) && now - last < 2000)
            return;
        _paperdollThrottle[ch.Uid.Value] = now;

        string title = string.IsNullOrEmpty(ch.Title)
            ? ch.GetName()
            : $"{ch.GetName()}, {ch.Title}";
        byte paperdollFlags = 0;
        if (_character != null && ch == _character) paperdollFlags |= 0x01; // can edit (own paperdoll)
        _netState.Send(new PacketOpenPaperdoll(ch.Uid.Value, title, paperdollFlags));

        SendCharacterStatus(ch, includeExtendedStats: ch == _character);
    }

    private void RefreshBackpackContents()
    {
        if (_character == null) return;
        var pack = _character.Backpack;
        if (pack == null) return;

        foreach (var child in _world.GetContainerContents(pack.Uid))
        {
            _netState.Send(new PacketContainerItem(
                child.Uid.Value, child.DispIdFull, 0,
                child.Amount, child.X, child.Y,
                pack.Uid.Value, child.Hue,
                _netState.IsClientPost6017));
        }
    }

    public void SendCharacterStatus(Character ch, bool includeExtendedStats = true)
    {
        // Expansion level matched to client version capabilities
        byte expansion;
        if (_netState.IsClientPost7090)
            expansion = 5; // ML (SA client can handle ML fields)
        else if (_netState.IsClientPost6017)
            expansion = 4; // SE
        else if (_netState.ClientVersionNumber >= 40_000_000)
            expansion = 3; // AOS
        else
            expansion = 0; // pre-AOS
        string statusName = ResolveStatusName(ch);
        var (hits, maxHits) = NormalizeStatusPair(ch.Hits, ch.MaxHits, ch.Str);
        var (stam, maxStam) = NormalizeStatusPair(ch.Stam, ch.MaxStam, ch.Dex);
        var (mana, maxMana) = NormalizeStatusPair(ch.Mana, ch.MaxMana, ch.Int);

        int gold = 0;
        var pack = ch.Backpack;
        if (pack != null)
            foreach (var gi in pack.Contents)
                if (gi.BaseId == 0x0EED) gold += gi.Amount;

        _netState.Send(new PacketStatusFull(
            ch.Uid.Value, statusName,
            hits, maxHits,
            ch.Str, ch.Dex, ch.Int,
            stam, maxStam, mana, maxMana,
            gold, (ushort)0, (ushort)0,
            ch.Fame, ch.Karma, 0, expansion
        ));

        // Keep self bars synchronized on clients that rely on A1/A2/A3 updates.
        if (_character != null && ch == _character)
        {
            _netState.Send(new PacketUpdateHealth(ch.Uid.Value, maxHits, hits));
            _netState.Send(new PacketUpdateMana(ch.Uid.Value, maxMana, mana));
            _netState.Send(new PacketUpdateStamina(ch.Uid.Value, maxStam, stam));
        }
    }

    private static uint StableStringHash(string s)
    {
        uint hash = 5381;
        foreach (char c in s)
            hash = ((hash << 5) + hash) ^ c;
        return hash;
    }

    private static (short Cur, short Max) NormalizeStatusPair(short cur, short max, short fallbackBase)
    {
        short safeMax = max > 0 ? max : (short)Math.Max(1, (int)fallbackBase);
        short safeCur = (short)Math.Clamp(cur, (short)0, safeMax);
        return (safeCur, safeMax);
    }

    private void BroadcastDeleteObject(uint uid)
    {
        _netState.Send(new PacketDeleteObject(uid));
        _knownChars.Remove(uid);
        _knownItems.Remove(uid);
        _lastKnownPos.Remove(uid);
        // excludeUid must be the CHARACTER's UID (not the deleted object's UID)
        // so the sending client is excluded from the broadcast (already got direct send).
        BroadcastNearby?.Invoke(_character?.Position ?? Point3D.Zero, UpdateRange, new PacketDeleteObject(uid), _character?.Uid.Value ?? 0);
    }

    private void BroadcastDrawObject(Character ch)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);
        var drawObj = new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, ch.Hue, flags, noto,
            equipment);
        _netState.Send(drawObj);
        // Use BroadcastMoveNearby (if available) to also update receiving clients'
        // _lastKnownPos cache, preventing a duplicate 0x77 from the next view delta.
        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby.Invoke(ch.Position, UpdateRange, drawObj, _character?.Uid.Value ?? 0, ch);
        else
            BroadcastNearby?.Invoke(ch.Position, UpdateRange, drawObj, _character?.Uid.Value ?? 0);
    }

    /// <summary>Resolves a target serial to a <see cref="Character"/>.
    /// If the serial is a corpse, falls back to the corpse's
    /// <c>OWNER_UID</c> tag (set by <see cref="DeathEngine"/>).</summary>
    private Character? ResolvePickedChar(uint uid)
    {
        if (uid == 0 || uid == 0xFFFFFFFF) return null;
        var ch = _world.FindChar(new Serial(uid));
        if (ch != null) return ch;
        var corpse = _world.FindItem(new Serial(uid));
        if (corpse != null && corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
            uint.TryParse(ownerStr, out uint ownerUid))
            return _world.FindChar(new Serial(ownerUid));
        return null;
    }

    /// <summary>Source-X CV_NUKE / CV_NUKECHAR / CV_NUDGE area
    /// implementation. Iterates the world sectors around
    /// <paramref name="centre"/> at <paramref name="range"/> tiles and
    /// applies the verb. Returns the number of objects affected.</summary>
    private int ExecuteAreaVerb(string verb, Point3D centre, int range)
    {
        if (_character == null) return 0;
        int affected = 0;
        switch (verb)
        {
            case "NUKE":
            {
                // Snapshot first — DeleteObject mutates the sector lists.
                var items = _world.GetItemsInRange(centre, range).ToList();
                foreach (var item in items)
                {
                    if (item.IsEquipped) continue;          // GM gear safe
                    if (item.ContainedIn.IsValid) continue; // bag contents safe
                    BroadcastDeleteObject(item.Uid.Value);
                    _world.DeleteObject(item);
                    item.Delete();
                    affected++;
                }
                break;
            }
            case "NUKECHAR":
            {
                var chars = _world.GetCharsInRange(centre, range).ToList();
                foreach (var ch in chars)
                {
                    if (ch == _character) continue;
                    if (ch.IsPlayer) continue;              // never auto-purge real players
                    BroadcastDeleteObject(ch.Uid.Value);
                    _world.DeleteObject(ch);
                    ch.Delete();
                    affected++;
                }
                break;
            }
            case "NUDGE":
            {
                // Source-X reads TARG.X/Y/Z TAGs as the displacement.
                // We default to (0, 0, +1) when the GM has not set them
                // — useful for "lift items 1 tile up to clear floors".
                int dx = TryGetIntTag("NUDGE.DX", 0);
                int dy = TryGetIntTag("NUDGE.DY", 0);
                int dz = TryGetIntTag("NUDGE.DZ", 1);
                if (dx == 0 && dy == 0 && dz == 0) dz = 1;
                foreach (var item in _world.GetItemsInRange(centre, range).ToList())
                {
                    if (item.ContainedIn.IsValid) continue;
                    var p = item.Position;
                    var np = new Point3D(
                        (short)(p.X + dx),
                        (short)(p.Y + dy),
                        (sbyte)(p.Z + dz),
                        p.Map);
                    BroadcastDeleteObject(item.Uid.Value);
                    _world.PlaceItem(item, np);
                    BroadcastNearby?.Invoke(np, UpdateRange,
                        new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                            item.X, item.Y, item.Z, item.Hue), 0);
                    affected++;
                }
                break;
            }
        }
        return affected;
    }

    private int TryGetIntTag(string key, int defaultValue)
    {
        if (_character != null && _character.TryGetTag(key, out string? v) &&
            int.TryParse(v, out int n))
            return n;
        return defaultValue;
    }

    /// <summary>Source-X CV_DUPE: clones an item next to the original.
    /// Copies BaseId, Hue and Amount; container/equipped duplicates are
    /// not supported (matches Source-X CClient_functions.tbl behaviour
    /// for items in the world).</summary>
    private Item? DuplicateItem(Item src)
    {
        if (_character == null) return null;
        var dup = _world.CreateItem();
        dup.BaseId = src.BaseId;
        dup.Hue = src.Hue;
        dup.Amount = src.Amount > 0 ? src.Amount : (ushort)1;
        if (!string.IsNullOrEmpty(src.Name)) dup.Name = src.Name;
        if (src.ContainedIn.IsValid)
            PlaceItemInPack(_character, dup);
        else
            _world.PlaceItem(dup, src.Position);
        BroadcastNearby?.Invoke(dup.Position, UpdateRange,
            new PacketWorldItem(dup.Uid.Value, dup.DispIdFull, dup.Amount,
                dup.X, dup.Y, dup.Z, dup.Hue), 0);
        return dup;
    }

    /// <summary>Source-X CV_SUMMONCAGE helper: drop iron-bar items in
    /// the 8 tiles surrounding <paramref name="centre"/> so the victim
    /// can't walk away. The bars are real items (visible to the client)
    /// and persist until manually removed.</summary>
    private void SpawnCageAround(Point3D centre)
    {
        // Bar graphics:  0x0084 vertical, 0x0086 horizontal (Source-X
        // i_bars_v / i_bars_h). We skip diagonal corners — the picture
        // is a "+" pattern around the victim, enough to block movement.
        var ring = new (short dx, short dy, ushort gfx)[]
        {
            ( 0, -1, 0x0086), ( 0,  1, 0x0086),
            (-1,  0, 0x0084), ( 1,  0, 0x0084),
        };
        foreach (var (dx, dy, gfx) in ring)
        {
            var bar = _world.CreateItem();
            bar.BaseId = gfx;
            bar.Amount = 1;
            var p = new Point3D((short)(centre.X + dx), (short)(centre.Y + dy),
                centre.Z, centre.Map);
            _world.PlaceItem(bar, p);
            BroadcastNearby?.Invoke(p, UpdateRange,
                new PacketWorldItem(bar.Uid.Value, bar.DispIdFull, bar.Amount,
                    bar.X, bar.Y, bar.Z, bar.Hue), 0);
        }
    }

    /// <summary>Source-X CV_BANK with a target arg: opens the picked
    /// character's bank box on this client. Mirrors CChar::Use_Obj
    /// for BankBox layer items.</summary>
    private void OpenForeignBank(Character victim)
    {
        var bank = victim.GetEquippedItem(Layer.BankBox);
        if (bank == null)
        {
            // Conjure a transient empty bank box so the GM still gets a UI.
            OpenBankBox();
            return;
        }
        // Reuse the standard open-container flow on the bank serial.
        _netState.Send(new PacketOpenContainer(bank.Uid.Value, 0x003C));
    }

    private bool RemoveTargetedObject(uint uid)
    {
        if (_character == null)
            return false;
        if (uid == _character.Uid.Value)
            return false;

        var item = _world.FindItem(new Serial(uid));
        if (item != null)
        {
            _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            BroadcastDeleteObject(uid);
            _world.DeleteObject(item);
            item.Delete();
            return true;
        }

        var ch = _world.FindChar(new Serial(uid));
        if (ch != null)
        {
            if (ch == _character)
                return false;

            _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Destroy,
                new TriggerArgs { CharSrc = _character });
            BroadcastDeleteObject(uid);
            _world.DeleteObject(ch);
            ch.Delete();
            return true;
        }

        return false;
    }

    private bool TryAddAtTarget(string token, Point3D targetPos, uint targetSerial = 0)
    {
        if (_character == null || _commands?.Resources == null)
            return false;

        string cleaned = token
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (cleaned.Length == 0)
            return false;

        var resources = _commands.Resources;
        var rid = resources.ResolveDefName(cleaned);
        if (rid.IsValid)
        {
            if (rid.Type == ResType.ItemDef)
            {
                ushort dispId = ResolveItemDispId(rid.Index);
                if (dispId == 0)
                {
                    SysMessage(ServerMessages.GetFormatted("gm_item_no_graphic", cleaned));
                    return true;
                }

                var item = _world.CreateItem();
                item.BaseId = dispId;
                item.Name = cleaned;

                var namedDef = DefinitionLoader.GetItemDef(rid.Index);
                if (namedDef != null)
                {
                    item.ItemType = namedDef.Type;
                    if (!string.IsNullOrWhiteSpace(namedDef.Name))
                        item.Name = DefinitionLoader.ResolveNames(namedDef.Name);
                    foreach (var ev in namedDef.Events)
                        if (!item.Events.Contains(ev))
                            item.Events.Add(ev);

                    if (rid.Index != dispId)
                        item.SetTag("SCRIPTDEF", rid.Index.ToString());
                }

                PlaceAddedItem(item, targetPos, targetSerial);
                SysMessage(ServerMessages.GetFormatted("gm_item_created", cleaned, $"{dispId:X}"));
                return true;
            }

            if (rid.Type == ResType.CharDef)
            {
                var npc = CreateNpcFromDef(rid.Index, cleaned);
                _logger.LogDebug(
                    "[npc_spawn] BEFORE @Create: def='{Def}' STR={Str} MaxHits={MH} Hits={H} DEX={Dex} INT={Int}",
                    cleaned, npc.Str, npc.MaxHits, npc.Hits, npc.Dex, npc.Int);
                _world.PlaceCharacter(npc, targetPos);
                var preCreateBrain = npc.NpcBrain;
                _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.Create, new TriggerArgs { CharSrc = _character });
                _logger.LogDebug(
                    "[npc_spawn] AFTER @Create: def='{Def}' STR={Str} MaxHits={MH} Hits={H}",
                    cleaned, npc.Str, npc.MaxHits, npc.Hits);
                FinalizeNpcBrain(npc);
                _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.CreateLoot, new TriggerArgs { CharSrc = _character });
                npc.Hits = npc.MaxHits;
                npc.Stam = npc.MaxStam;
                npc.Mana = npc.MaxMana;
                _logger.LogDebug(
                    "[npc_spawn] AFTER @CreateLoot: def='{Def}' STR={Str} MaxHits={MH} Hits={H} brain={Brain}",
                    cleaned, npc.Str, npc.MaxHits, npc.Hits, npc.NpcBrain);
                BroadcastDrawObject(npc);
                SysMessage(ServerMessages.GetFormatted("gm_npc_created2", npc.Name, $"{rid.Index:X}", targetPos));
                return true;
            }
        }

        string num = cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? cleaned[2..] : cleaned;
        bool parsed = ushort.TryParse(num, System.Globalization.NumberStyles.HexNumber, null, out ushort idHex) ||
                      ushort.TryParse(cleaned, out idHex);
        if (!parsed)
            return false;

        bool hasItemDef = resources.GetResource(ResType.ItemDef, idHex) != null;
        bool hasCharDef = resources.GetResource(ResType.CharDef, idHex) != null;
        if (hasItemDef || !hasCharDef)
        {
            var item = _world.CreateItem();
            item.BaseId = idHex;
            item.Name = $"Item_{idHex:X}";
            PlaceAddedItem(item, targetPos, targetSerial);
            SysMessage(ServerMessages.GetFormatted("gm_item_created_hex", $"{idHex:X}", targetPos));
            return true;
        }

        var createdNpc = CreateNpcFromDef(idHex, $"NPC_{idHex:X}");
        _world.PlaceCharacter(createdNpc, targetPos);
        _triggerDispatcher?.FireCharTrigger(createdNpc, CharTrigger.Create, new TriggerArgs { CharSrc = _character });
        FinalizeNpcBrain(createdNpc);
        _triggerDispatcher?.FireCharTrigger(createdNpc, CharTrigger.CreateLoot, new TriggerArgs { CharSrc = _character });
        createdNpc.Hits = createdNpc.MaxHits;
        createdNpc.Stam = createdNpc.MaxStam;
        createdNpc.Mana = createdNpc.MaxMana;
        BroadcastDrawObject(createdNpc);
        SysMessage(ServerMessages.GetFormatted("gm_npc_created_hex", createdNpc.Name, $"{idHex:X}", targetPos));
        return true;
    }

    private void PlaceAddedItem(Item item, Point3D groundPos, uint targetSerial)
    {
        if (targetSerial != 0 && targetSerial != 0xFFFFFFFF)
        {
            var targetChar = _world.FindChar(new Serial(targetSerial));
            if (targetChar != null)
            {
                PlaceItemInPack(targetChar, item);
                return;
            }
            var targetContainer = _world.FindItem(new Serial(targetSerial));
            if (targetContainer != null && targetContainer.ItemType is ItemType.Container or ItemType.ContainerLocked)
            {
                targetContainer.AddItem(item);
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, item.X, item.Y,
                    targetContainer.Uid.Value, item.Hue,
                    _netState.IsClientPost6017));
                return;
            }
        }
        _world.PlaceItem(item, groundPos);
    }

    /// <summary>Walk the ITEMDEF chain to find the concrete UO art ID.
    /// A scripted itemdef may set <c>id=</c> to another defname that in
    /// turn resolves to a hex graphic (common Sphere pattern:
    /// <c>[itemdef i_moongate] id=i_moongate_blue</c>, where
    /// <c>i_moongate_blue</c> is <c>[itemdef 0f6c]</c>). Returns 0 when
    /// no numeric graphic can be reached within a small lookup bound —
    /// the caller treats that as "unknown graphic" and aborts the add.</summary>
    private static ushort ResolveItemDispId(int defIndex)
    {
        for (int hop = 0; hop < 8; hop++)
        {
            var d = DefinitionLoader.GetItemDef(defIndex);

            // Numeric itemdef ([ITEMDEF 0f6c]) is keyed by its hex value.
            // If no def exists at that hex, the index IS the graphic.
            if (d == null) return (ushort)(defIndex & 0xFFFF);

            // Def exists but has no explicit ID/DISPID. For numeric-range
            // sections (<= 0xFFFF) the section header itself is the
            // graphic — treat defIndex as the graphic. For hash-range
            // (named) sections without a DispIndex, we truly can't
            // resolve a graphic and the add fails.
            if (d.DispIndex == 0)
                return defIndex <= 0xFFFF ? (ushort)defIndex : (ushort)0;

            // DispIndex may itself point to another named itemdef (hash
            // index that resolves through _itemDefs). Follow the chain.
            if (DefinitionLoader.GetItemDef(d.DispIndex) is { } next && next != d)
            {
                defIndex = d.DispIndex;
                continue;
            }
            return d.DispIndex;
        }
        return 0;
    }

    private Character CreateNpcFromDef(int defIndexOrBaseId, string fallbackName)
    {
        var npc = _world.CreateCharacter();
        ushort safeBaseId = (ushort)Math.Clamp(defIndexOrBaseId, 0, ushort.MaxValue);
        npc.BaseId = safeBaseId;
        // Trigger / CharDef lookups need the full 24-bit defname hash, the
        // ushort BaseId truncates it and routes c_alchemist's @Create to
        // c_man (brain=Human) and misses c_banker entirely (brain=Animal).
        npc.CharDefIndex = defIndexOrBaseId;
        npc.Name = fallbackName;
        npc.BodyId = safeBaseId;
        npc.IsPlayer = false;

        var charDef = DefinitionLoader.GetCharDef(defIndexOrBaseId);
        if (charDef != null)
        {
            ushort resolvedBody = ResolveCharBodyId(charDef, safeBaseId);
            npc.BodyId = resolvedBody;
            // BaseId mirrors the resolved display body for legacy
            // consumers (mounting / click behaviour). Trigger / CharDef
            // lookups now go through CharDefIndex (full 24-bit defname
            // hash), so the c_alchemist→c_man aliasing no longer
            // hijacks @Create or brain selection.
            npc.BaseId = resolvedBody;
            if (!string.IsNullOrWhiteSpace(charDef.Name))
                npc.Name = DefinitionLoader.ResolveNames(charDef.Name);

            int strVal = charDef.StrMax > 0 ? charDef.StrMax : Math.Max(1, charDef.StrMin);
            int dexVal = charDef.DexMax > 0 ? charDef.DexMax : Math.Max(1, charDef.DexMin);
            int intVal = charDef.IntMax > 0 ? charDef.IntMax : Math.Max(1, charDef.IntMin);

            npc.Str = (short)Math.Clamp(strVal, 1, short.MaxValue);
            npc.Dex = (short)Math.Clamp(dexVal, 1, short.MaxValue);
            npc.Int = (short)Math.Clamp(intVal, 1, short.MaxValue);

            int hits = charDef.HitsMax > 0 ? charDef.HitsMax : Math.Max(1, strVal);
            short maxHits = (short)Math.Clamp(hits, 1, short.MaxValue);
            npc.MaxHits = maxHits;
            npc.Hits = maxHits;
            npc.MaxMana = npc.Int;
            npc.Mana = npc.Int;
            npc.MaxStam = npc.Dex;
            npc.Stam = npc.Dex;

            if (charDef.NpcBrain != NpcBrainType.None)
                npc.NpcBrain = charDef.NpcBrain;

            string? colorText = charDef.TagDefs.Get("COLOR");
            if (TryParseHue(colorText, out ushort hue))
                npc.Hue = new Color(hue);

            // Elemental damage percentages
            if (charDef.DamFire != 0) npc.DamFire = charDef.DamFire;
            if (charDef.DamCold != 0) npc.DamCold = charDef.DamCold;
            if (charDef.DamPoison != 0) npc.DamPoison = charDef.DamPoison;
            if (charDef.DamEnergy != 0) npc.DamEnergy = charDef.DamEnergy;
            if (charDef.DamPhysical != 0) npc.DamPhysical = charDef.DamPhysical;
            else if (charDef.DamFire != 0 || charDef.DamCold != 0 || charDef.DamPoison != 0 || charDef.DamEnergy != 0)
                npc.DamPhysical = (short)(100 - charDef.DamFire - charDef.DamCold - charDef.DamPoison - charDef.DamEnergy);

            EquipNewbieItems(npc, charDef.NewbieItems);
        }
        else
        {
            npc.Str = 50; npc.Dex = 50; npc.Int = 50;
            npc.MaxHits = 50; npc.Hits = 50;
            npc.MaxMana = 50; npc.Mana = 50;
            npc.MaxStam = 50; npc.Stam = 50;
        }

        // Brain finalisation (Animal fallback + @NPCRestock for vendors)
        // intentionally happens AFTER @Create runs — see FinalizeNpcBrain.
        // Sphere scripts set NPC=brain_vendor inside ON=@Create, so the brain
        // is only known once that trigger has executed.

        return npc;
    }

    /// <summary>
    /// Apply the post-@Create brain rules: default to Animal when nothing
    /// set a brain, and fire @NPCRestock for vendors so they come stocked.
    /// Call this AFTER FireCharTrigger(Create), never before.
    /// </summary>
    private void FinalizeNpcBrain(Character npc)
    {
        if (npc.NpcBrain == NpcBrainType.None)
            npc.NpcBrain = NpcBrainType.Animal;

        if (npc.NpcBrain == NpcBrainType.Vendor)
        {
            _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCRestock,
                new TriggerArgs { CharSrc = npc });
        }
    }

    private ushort ResolveCharBodyId(CharDef charDef, ushort fallbackBaseId)
    {
        if (charDef.DispIndex > 0)
            return charDef.DispIndex;

        string alias = charDef.DisplayIdRef?.Trim() ?? "";
        if (alias.Length == 0 || _commands?.Resources == null)
            return fallbackBaseId;

        var rid = _commands.Resources.ResolveDefName(alias);
        if (rid.IsValid && rid.Type == ResType.CharDef)
        {
            var refDef = DefinitionLoader.GetCharDef(rid.Index);
            if (refDef?.DispIndex > 0)
                return refDef.DispIndex;

            if (rid.Index >= 0 && rid.Index <= ushort.MaxValue)
                return (ushort)rid.Index;
        }

        return fallbackBaseId;
    }

    private static bool TryParseHue(string? value, out ushort hue)
    {
        hue = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(v[2..], System.Globalization.NumberStyles.HexNumber, null, out hue);
        if (v.StartsWith('0') && v.Length > 1 &&
            ushort.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out ushort hexHue))
        {
            hue = hexHue;
            return true;
        }
        return ushort.TryParse(v, out hue);
    }

    private void EquipNewbieItems(Character ch, IReadOnlyList<NewbieItemEntry> newbieItems)
    {
        if (newbieItems.Count == 0 || _commands?.Resources == null)
            return;

        var resources = _commands.Resources;
        ushort lastHue = 0; // for COLOR=match_hair / match_*
        foreach (var entry in newbieItems)
        {
            // Resolve random_* / weighted-template pools to a single itemdef.
            string pickedName = TemplateEngine.PickRandomItemDefName(entry.DefName);
            if (string.IsNullOrWhiteSpace(pickedName))
                continue;

            var rid = resources.ResolveDefName(pickedName);
            if (!rid.IsValid || rid.Type != ResType.ItemDef)
                continue;

            var item = _world.CreateItem();
            var itemDef = DefinitionLoader.GetItemDef(rid.Index);
            // Defname ITEMDEFs (i_shirt_plain, …) live under a 32-bit
            // string hash, but their wire graphic is in DispIndex —
            // see TemplateEngine.ResolveDispId. Truncating rid.Index
            // to ushort previously gave newbies random-tile clothing
            // (lava breastplates, window-shutter shirts).
            ushort dispId = 0;
            if (itemDef != null)
            {
                if (itemDef.DispIndex != 0) dispId = itemDef.DispIndex;
                else if (itemDef.DupItemId != 0) dispId = itemDef.DupItemId;
            }
            if (dispId == 0 && rid.Index <= 0xFFFF) dispId = (ushort)rid.Index;
            if (dispId == 0) continue;
            item.BaseId = dispId;

            // Store raw NAME= template; Item.GetName() resolves
            // %plural/singular% markers per Amount on every read.
            if (itemDef != null && !string.IsNullOrWhiteSpace(itemDef.Name))
                item.Name = itemDef.Name;

            // Amount: explicit wins; else dice roll; else leave default (1).
            int amount = entry.Amount;
            if (amount <= 0 && !string.IsNullOrWhiteSpace(entry.Dice))
                amount = RollSphereDice(entry.Dice);
            if (amount > 1)
                item.Amount = (ushort)Math.Min(amount, ushort.MaxValue);

            // Color: colors_* defname → random hue from the range;
            // "match_<prev>" → re-use the last resolved hue so a hair /
            // beard pair share the tint.
            if (!string.IsNullOrWhiteSpace(entry.Color))
            {
                ushort hue = ResolveColorDefName(entry.Color!, lastHue);
                if (hue != 0)
                {
                    item.Hue = new Color(hue);
                    lastHue = hue;
                }
            }

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
            if (layer == Layer.None)
            {
                var pack = ch.Backpack;
                if (pack == null)
                {
                    pack = _world.CreateItem();
                    pack.BaseId = 0x0E75;
                    ch.Equip(pack, Layer.Pack);
                }
                pack.AddItem(item);
            }
            else
            {
                ch.Equip(item, layer);
            }
        }
    }

    /// <summary>Very small Sphere dice roller. Supports R&lt;max&gt;
    /// (1..max) and NdM (N M-sided). Anything unrecognised falls back
    /// to 1 so a broken script line never silently spawns a 0-amount
    /// item.</summary>
    private static int RollSphereDice(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return 1;
        if ((expr[0] == 'R' || expr[0] == 'r') &&
            int.TryParse(expr.AsSpan(1), out int max) && max > 0)
            return Random.Shared.Next(1, max + 1);
        int dIdx = expr.IndexOf('d');
        if (dIdx < 0) dIdx = expr.IndexOf('D');
        if (dIdx > 0 &&
            int.TryParse(expr.AsSpan(0, dIdx), out int n) && n > 0 &&
            int.TryParse(expr.AsSpan(dIdx + 1), out int sides) && sides > 0)
        {
            int total = 0;
            for (int i = 0; i < n; i++) total += Random.Shared.Next(1, sides + 1);
            return total;
        }
        return int.TryParse(expr, out int literal) && literal > 0 ? literal : 1;
    }

    /// <summary>Resolve a <c>colors_*</c> / <c>match_*</c> defname to an
    /// actual hue value. Source-X defines <c>colors_skin</c>,
    /// <c>colors_hair</c>, <c>colors_red</c> etc. as DEF[NAME] entries
    /// containing <c>{low high}</c> ranges; we mirror the common ones
    /// inline so scripts using the standard palette work without the
    /// color-defs scp file. Unknown names fall through to numeric parse.</summary>
    private static ushort ResolveColorDefName(string name, ushort lastHue)
    {
        string n = name.Trim();
        if (string.IsNullOrEmpty(n)) return 0;
        // match_hair / match_skin / match_* → use the previously picked hue.
        if (n.StartsWith("match_", StringComparison.OrdinalIgnoreCase))
            return lastHue;

        // Canonical palette ranges (inclusive) — matches classic
        // Sphere defaults shipped with the standard script pack.
        (ushort lo, ushort hi) = n.ToLowerInvariant() switch
        {
            "colors_skin" => ((ushort)0x03EA, (ushort)0x03F2),
            "colors_hair" => ((ushort)0x044E, (ushort)0x0455),
            "colors_red" => ((ushort)0x0020, (ushort)0x002C),
            "colors_orange" => ((ushort)0x002D, (ushort)0x0038),
            "colors_yellow" => ((ushort)0x0039, (ushort)0x0044),
            "colors_green" => ((ushort)0x0059, (ushort)0x0062),
            "colors_blue" => ((ushort)0x0053, (ushort)0x0058),
            "colors_purple" => ((ushort)0x0010, (ushort)0x001E),
            "colors_neutral" => ((ushort)0x03B0, (ushort)0x03B4),
            "colors_all" => ((ushort)0x0002, (ushort)0x03E9),
            _ => ((ushort)0, (ushort)0),
        };
        if (lo != 0 || hi != 0)
            return (ushort)Random.Shared.Next(lo, hi + 1);

        // Last resort: numeric hex/dec literal (COLOR=0x0481 style).
        if (TryParseHue(n, out ushort direct))
            return direct;
        return 0;
    }
}
