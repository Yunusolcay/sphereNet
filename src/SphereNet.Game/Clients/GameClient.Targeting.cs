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

    public void HandleTargetResponse(byte type, uint targetId, uint serial, short x, short y, sbyte z, ushort graphic)
    {
        if (_character == null) return;
        _targetCursorActive = false;
        bool targetCancelled = IsTargetCancelled(serial, x, y, z, graphic);
        if (targetCancelled)
        {
            // Hard-cancel all pending target flows to avoid any stale state from triggering
            // a resync/teleport path on the next target packet.
            _pendingTeleTarget = false;
            _pendingAddToken = null;
            _pendingRemoveTarget = false;
            _pendingXVerb = null;
            _pendingXVerbArgs = "";
            _pendingAreaVerb = null;
            _pendingAreaRange = 0;
            _pendingControlTarget = false;
            _pendingDupeTarget = false;
            _pendingHealTarget = false;
            _pendingKillTarget = false;
            _pendingBankTarget = false;
            _pendingSummonToTarget = false;
            _pendingMountTarget = false;
            _pendingSummonCageTarget = false;
            _pendingTargetFunction = null;
            _pendingTargetArgs = "";
            _pendingTargetAllowGround = false;
            _pendingTargetItemUid = Serial.Invalid;
            _pendingScriptNewItem = null;
            _lastScriptTargetPoint = null;
            _pendingTargetCallback = null;

            if (_character.TryGetTag("CAST_SPELL", out _))
                _character.RemoveTag("CAST_SPELL");
            _character.RemoveTag("TARGP");
            _character.RemoveTag("TARG.X");
            _character.RemoveTag("TARG.Y");
            _character.RemoveTag("TARG.Z");
            _character.RemoveTag("TARG.MAP");
            _character.RemoveTag("TARG.UID");

            SysMessage(ServerMessages.Get("target_cancel_1"));
            return;
        }

        // Callback-based target (housing, etc.)
        if (_pendingTargetCallback != null)
        {
            var cb = _pendingTargetCallback;
            _pendingTargetCallback = null;
            cb(serial, x, y, z, graphic);
            return;
        }

        if (_pendingTeleTarget)
        {
            _pendingTeleTarget = false;

            Point3D? destination = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var obj = _world.FindObject(new Serial(serial));
                if (obj is Character targetChar)
                {
                    destination = targetChar.Position;
                }
                else if (obj is Item targetItem)
                {
                    destination = targetItem.Position;
                }
            }

            destination ??= new Point3D(x, y, z, _character.MapIndex);

            // Snap Z to the nearest walkable surface. Clients pick the Z of
            // whatever tile the mouse overlaps — frequently a rooftop or a
            // static plane. Landing there strands the player: every subsequent
            // step gets rejected by climb/cliff checks (~150 MoveReject spam
            // on `.mtele 1493,1639,40` observed in logs).
            var mdata = _world.MapData;
            if (mdata != null)
            {
                var d = destination.Value;
                sbyte walkZ = mdata.GetEffectiveZ(_character.MapIndex, d.X, d.Y, (sbyte)d.Z);
                if (walkZ != d.Z)
                    destination = new Point3D(d.X, d.Y, walkZ, _character.MapIndex);
            }

            _world.MoveCharacter(_character, destination.Value);
            Resync();
            _mountEngine?.EnsureMountedState(_character);
            // Broadcast full appearance (including mount) to nearby clients at new location.
            BroadcastDrawObject(_character);
            SysMessage(ServerMessages.GetFormatted("gm_teleported_dest", destination.Value));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingAddToken))
        {
            string addToken = _pendingAddToken;
            _pendingAddToken = null;

            Point3D targetPos = new Point3D(x, y, z, _character.MapIndex);
            uint targetSerial = serial;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var obj = _world.FindObject(new Serial(serial));
                if (obj != null)
                    targetPos = obj.Position;
            }

            if (!TryAddAtTarget(addToken, targetPos, targetSerial))
                SysMessage(ServerMessages.GetFormatted("gm_unknown_add", addToken));
            return;
        }

        if (_pendingRemoveTarget)
        {
            _pendingRemoveTarget = false;

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            if (RemoveTargetedObject(serial))
                SysMessage(ServerMessages.GetFormatted("gm_removed", $"{serial:X8}"));
            else
                SysMessage(ServerMessages.Get("target_cant_remove"));
            return;
        }

        if (_pendingResurrectTarget)
        {
            _pendingResurrectTarget = false;

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            // Try the picked serial as a character first; if it's a corpse,
            // fall back to the OWNER_UID tag the DeathEngine wrote on it.
            var victim = _world.FindChar(new Serial(serial));
            if (victim == null)
            {
                var corpse = _world.FindItem(new Serial(serial));
                if (corpse != null && corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
                    uint.TryParse(ownerStr, out uint ownerUid))
                {
                    victim = _world.FindChar(new Serial(ownerUid));
                }
            }

            if (victim == null)
            {
                SysMessage("Resurrect: cannot identify a character from that target.");
                return;
            }
            if (!victim.IsDead)
            {
                SysMessage($"'{victim.Name}' is not dead.");
                return;
            }

            OnResurrectOther?.Invoke(victim);
            SysMessage($"Resurrected '{victim.Name}'.");
            return;
        }

        if (_pendingInspectTarget)
        {
            _pendingInspectTarget = false;
            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }
            var infoObj = _world.FindObject(new Serial(serial));
            if (infoObj != null)
                OpenInspectPropDialog(infoObj, 0);
            else
                SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{serial:X8}"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingShowArgs))
        {
            string showArgs = _pendingShowArgs;
            _pendingShowArgs = null;

            if (_commands == null || serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            _commands.ExecuteShowForTarget(_character, showArgs, serial);
            return;
        }

        if (_pendingEditArgs != null)
        {
            string editArgs = _pendingEditArgs;
            _pendingEditArgs = null;

            if (_commands == null || serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            _commands.ExecuteEditForTarget(_character, editArgs, serial);
            return;
        }

        // ---- Phase C: NUKE / NUKECHAR / NUDGE area handlers ----
        if (!string.IsNullOrEmpty(_pendingAreaVerb))
        {
            string areaVerb = _pendingAreaVerb!;
            int areaRange = _pendingAreaRange;
            _pendingAreaVerb = null;
            _pendingAreaRange = 0;

            // Resolve the centre. If the GM clicked on an object use its
            // position so NUDGE/NUKE applied to a chest also covers the
            // surrounding tiles, mirroring Source-X's box centre behaviour.
            Point3D centre;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var picked = _world.FindObject(new Serial(serial));
                centre = picked?.Position ?? new Point3D(x, y, z, _character.MapIndex);
            }
            else
            {
                centre = new Point3D(x, y, z, _character.MapIndex);
            }

            int affected = ExecuteAreaVerb(areaVerb, centre, areaRange);
            switch (areaVerb)
            {
                case "NUKE":
                    SysMessage(ServerMessages.GetFormatted("gm_nuke_done", affected));
                    break;
                case "NUKECHAR":
                    SysMessage(ServerMessages.GetFormatted("gm_nukechar_done", affected));
                    break;
                case "NUDGE":
                    SysMessage(ServerMessages.GetFormatted("gm_nudge_done", affected));
                    break;
            }
            return;
        }

        if (_pendingControlTarget)
        {
            _pendingControlTarget = false;
            var npc = ResolvePickedChar(serial);
            if (npc == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            npc.TryAssignOwnership(_character, _character, summoned: false, enforceFollowerCap: false);
            SysMessage(ServerMessages.GetFormatted("gm_control_done", npc.Name));
            return;
        }

        if (_pendingDupeTarget)
        {
            _pendingDupeTarget = false;
            var pickedItem = serial != 0 && serial != 0xFFFFFFFF
                ? _world.FindItem(new Serial(serial))
                : null;
            if (pickedItem == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            var dup = DuplicateItem(pickedItem);
            if (dup != null)
                SysMessage(ServerMessages.GetFormatted("gm_dupe_done",
                    pickedItem.Name ?? "item", dup.Uid.Value.ToString("X8")));
            else
                SysMessage(ServerMessages.Get("target_cant_remove"));
            return;
        }

        if (_pendingHealTarget)
        {
            _pendingHealTarget = false;
            var victim = ResolvePickedChar(serial);
            if (victim == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            if (victim.IsDead) victim.Resurrect();
            victim.Hits = victim.MaxHits;
            victim.Mana = victim.MaxMana;
            victim.Stam = victim.MaxStam;
            SysMessage(ServerMessages.GetFormatted("gm_heal_done", victim.Name));
            return;
        }

        if (_pendingKillTarget)
        {
            _pendingKillTarget = false;
            var victim = ResolvePickedChar(serial);
            if (victim == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            OnKillTarget?.Invoke(_character!, victim);
            return;
        }

        if (_pendingBankTarget)
        {
            _pendingBankTarget = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            // We open the picked char's bank on *our* client. Source-X
            // Eq's the BankBox onto the picked char then sends the owner
            // GM a 0x24 OpenContainer; the bank items are then drawn
            // from that container's content.
            OpenForeignBank(picked);
            return;
        }

        if (_pendingSummonToTarget)
        {
            _pendingSummonToTarget = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            _world.MoveCharacter(picked, _character.Position);
            BroadcastDrawObject(picked);
            SysMessage(ServerMessages.GetFormatted("gm_summonto_done", picked.Name));
            return;
        }

        if (_pendingMountTarget)
        {
            _pendingMountTarget = false;
            var npc = ResolvePickedChar(serial);
            if (npc == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            if (_mountEngine == null || !_mountEngine.TryMount(_character, npc))
            {
                SysMessage(ServerMessages.Get("gm_mount_failed"));
                return;
            }
            BroadcastDrawObject(_character);
            SysMessage(ServerMessages.GetFormatted("gm_mount_done", npc.Name));
            return;
        }

        if (_pendingSummonCageTarget)
        {
            _pendingSummonCageTarget = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            // Source-X CV_SUMMONCAGE: teleport the victim to the GM and
            // ring them with iron-bar items. We use BaseId 0x0008/0x0009
            // for vertical/horizontal bars (DEFNAMES i_bars_*).
            _world.MoveCharacter(picked, _character.Position);
            BroadcastDrawObject(picked);
            SpawnCageAround(picked.Position);
            SysMessage(ServerMessages.GetFormatted("gm_summoncage_done", picked.Name));
            return;
        }

        // Source-X CClient.cpp:921 — generic X-prefix verb fallback:
        // resolve the picked object and apply the inner verb to it via
        // SpeechEngine.ExecuteVerbForTarget. Mirrors C++ addTargetVerb.
        if (!string.IsNullOrEmpty(_pendingXVerb))
        {
            string verb = _pendingXVerb!;
            string xargs = _pendingXVerbArgs;
            _pendingXVerb = null;
            _pendingXVerbArgs = "";

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            IScriptObj? obj = (IScriptObj?)_world.FindChar(new Serial(serial))
                ?? _world.FindItem(new Serial(serial));
            if (obj == null)
            {
                SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{serial:X8}"));
                return;
            }

            // Snapshot the relevant fields we may need to broadcast on
            // change (position / appearance) before mutating the target.
            Point3D? posBefore = (obj as Character)?.Position;
            ushort bodyBefore = (obj as Character)?.BodyId ?? 0;
            ushort hueBefore = (obj as Character)?.Hue.Value ?? 0;

            bool ok = _commands?.ExecuteVerbForTarget(_character, verb, xargs, obj) ?? false;

            if (ok)
            {
                SysMessage(ServerMessages.GetFormatted("gm_xverb_applied", verb, obj.GetName()));

                if (obj is Character ch)
                {
                    bool moved = posBefore.HasValue && !ch.Position.Equals(posBefore.Value);
                    bool appearance = ch.BodyId != bodyBefore || ch.Hue.Value != hueBefore;
                    if (moved)
                    {
                        _world.MoveCharacter(ch, ch.Position);
                        if (ch == _character) Resync();
                        BroadcastDrawObject(ch);
                    }
                    else if (appearance)
                    {
                        BroadcastDrawObject(ch);
                    }
                }
            }
            else
            {
                SysMessage(ServerMessages.GetFormatted("gm_xverb_failed", verb, obj.GetName()));
            }
            return;
        }

        if (!string.IsNullOrEmpty(_pendingTargetFunction) && _triggerDispatcher?.Runner != null)
        {
            string func = _pendingTargetFunction;
            _pendingTargetFunction = null;
            bool allowGround = _pendingTargetAllowGround;
            _pendingTargetAllowGround = false;
            var pendingItemUid = _pendingTargetItemUid;
            _pendingTargetItemUid = Serial.Invalid;
            _lastScriptTargetPoint = new Point3D(x, y, z, _character.MapIndex);
            _character.SetTag("TARGP", $"{x},{y},{z},{_character.MapIndex}");
            _character.SetTag("TARG.X", x.ToString());
            _character.SetTag("TARG.Y", y.ToString());
            _character.SetTag("TARG.Z", z.ToString());
            _character.SetTag("TARG.MAP", _character.MapIndex.ToString());
            _character.SetTag("TARG.UID", $"0{serial:X}");

            IScriptObj? argo = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
                argo = _world.FindObject(new Serial(serial));
            if (argo == null && !allowGround)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            var trigArgs = new ExecTriggerArgs(_character, 0, 0, _pendingTargetArgs)
            {
                Object1 = argo,
                Object2 = pendingItemUid.IsValid
                    ? ((IScriptObj?)_world.FindItem(pendingItemUid) ?? _character)
                    : _character
            };
            _pendingTargetArgs = "";

            // Snapshot position before running the script function so we can
            // detect if it moved the character (e.g. SRC.GO <TARGP>).
            // We cannot rely on _lastScriptTargetPoint because the script may
            // chain another TARGETF which calls ClearPendingTargetState and
            // clears _lastScriptTargetPoint before we get back here.
            var posBefore = _character.Position;
            _triggerDispatcher.Runner.RunFunction(func, _character, this, trigArgs);
            if (_character != null && !_character.Position.Equals(posBefore))
            {
                _world.MoveCharacter(_character, _character.Position);
                Resync();
                _mountEngine?.EnsureMountedState(_character);
                BroadcastDrawObject(_character);
            }
            return;
        }

        if (_character.TryGetTag("CAST_SPELL", out string? spellStr) &&
            Enum.TryParse<SpellType>(spellStr, out var spell))
        {
            _character.RemoveTag("CAST_SPELL");
            HandleCastSpell(spell, serial);
        }
    }

    private static bool IsTargetCancelled(uint serial, short x, short y, sbyte z, ushort graphic)
    {
        // Classic cancel payload variant (seen in some clients): serial=0, x=y=0xFFFF.
        if (serial == 0 && (ushort)x == 0xFFFF && (ushort)y == 0xFFFF)
            return true;

        // Client cancel is most commonly serial=0xFFFFFFFF.
        if (serial == 0xFFFFFFFF)
            return true;

        // Some clients send a fully-zero target payload on ESC.
        if (serial == 0 && x == 0 && y == 0 && z == 0 && graphic == 0)
            return true;

        // Legacy cancel payload variant with -1 coordinates.
        if (serial == 0 && x == -1 && y == -1 && z == -1)
            return true;

        // Additional client cancel variant: serial=0 with out-of-world x/y.
        // Note: z < 0 is valid (caves/dungeons), only reject impossible x/y.
        if (serial == 0 && (x < 0 || y < 0))
            return true;

        // Another observed cancel form: invalid serial + no model + max coords.
        if (serial == 0 && graphic == 0 && ((ushort)x == 0xFFFF || (ushort)y == 0xFFFF))
            return true;

        return false;
    }

    // ==================== Gump Response ====================

    public void HandleGumpResponse(uint serial, uint gumpId, uint buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null) return;

        if (!string.IsNullOrWhiteSpace(_pendingDialogCloseFunction))
        {
            string closeFn = _pendingDialogCloseFunction;
            _pendingDialogCloseFunction = null;
            var trigArgs = new ExecTriggerArgs(_character, (int)buttonId, (int)gumpId, _pendingDialogArgs)
            {
                Object1 = _character,
                Object2 = _character
            };

            // Allow script-provided close function tokens like CTAG0.HELP_TYPE.
            if (TryResolveScriptVariable(closeFn, _character, trigArgs, out string resolvedCloseFn) &&
                !string.IsNullOrWhiteSpace(resolvedCloseFn))
            {
                closeFn = resolvedCloseFn;
            }

            closeFn = closeFn.Trim().Trim(',', ';');
            if (closeFn.Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase) ||
                closeFn.Equals("DIALOGCLOSE()", StringComparison.OrdinalIgnoreCase))
            {
                closeFn = $"f_dialogclose_{_pendingDialogArgs}";
            }
            if (string.IsNullOrWhiteSpace(closeFn))
                closeFn = $"f_dialogclose_{_pendingDialogArgs}";

            _pendingDialogArgs = "";
            if (_triggerDispatcher?.Runner != null)
            {
                // Script-first fallback chain:
                // 1) explicit/variable-resolved close function
                // 2) default f_dialogclose_<dialogId>
                if (!_triggerDispatcher.Runner.TryRunFunction(closeFn, _character, this, trigArgs, out _))
                {
                    string defaultCloseFn = $"f_dialogclose_{trigArgs.ArgString.Trim().Trim(',', ';')}";
                    _triggerDispatcher.Runner.TryRunFunction(defaultCloseFn, _character, this, trigArgs, out _);
                }
            }
        }

        // Route to registered callback if present
        if (_gumpCallbacks.TryGetValue(gumpId, out var callback))
        {
            _gumpCallbacks.Remove(gumpId);
            callback(buttonId, switches, textEntries);
            return;
        }

        _logger.LogDebug("GumpResponse: serial=0x{S:X}, gumpId=0x{G:X}, button={B}",
            serial, gumpId, buttonId);
    }

    // ==================== Gump Sending ====================

    /// <summary>Send a gump dialog to the client. Optionally register a response callback.</summary>
    public void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null)
    {
        if (_character == null) return;

        if (callback != null)
            _gumpCallbacks[gump.GumpId] = callback;

        string layout = gump.BuildLayoutString();
        int gx = gump.ExplicitX ?? (gump.Width > 0 ? (800 - gump.Width) / 2 : 50);
        int gy = gump.ExplicitY ?? (gump.Height > 0 ? (600 - gump.Height) / 2 : 50);
        _netState.Send(new PacketGumpDialog(
            gump.Serial, gump.GumpId, gx, gy, layout, gump.Texts));
    }

    /// <summary>Set a callback-based target cursor. Used by housing, pets, etc.</summary>
    private void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1)
    {
        if (_targetCursorActive)
            _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));

        ClearPendingTargetState();
        _pendingTargetCallback = callback;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(cursorType, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    // ==================== Information Skills ====================
}
