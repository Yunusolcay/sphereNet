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

    /// <summary>Handle death menu response (0x2C).</summary>
    public void HandleDeathMenu(byte action)
    {
        if (_character == null) return;

        switch (action)
        {
            case 0: // Client requesting death menu — ignore, we already sent it
                break;
            case 1: // Resurrect
                _logger.LogDebug("[death_menu] char=0x{Uid:X8} chose resurrect", _character.Uid.Value);
                OnResurrect();
                break;
            case 2: // Stay as ghost
                _logger.LogDebug("[death_menu] char=0x{Uid:X8} chose ghost", _character.Uid.Value);
                break;
        }
    }

    /// <summary>Handle character delete from char select screen (0x83).</summary>
    public void HandleCharDelete(int charIndex, string password)
    {
        if (_account == null) return;

        // Verify password
        if (!_account.CheckPassword(password))
        {
            _netState.Send(new PacketCharDeleteResult(1)); // 1=bad password
            return;
        }

        var charUid = _account.GetCharSlot(charIndex);
        if (!charUid.IsValid)
        {
            _netState.Send(new PacketCharDeleteResult(1));
            return;
        }

        var ch = _world.FindChar(charUid);
        if (ch != null)
        {
            if (ch.IsOnline)
            {
                _netState.Send(new PacketCharDeleteResult(5)); // 5=char in world
                return;
            }

            _logger.LogInformation("Deleting character '{Name}' (0x{Uid:X8}) from account '{Acct}'",
                ch.Name, charUid.Value, _account.Name);
            ch.Delete();
            _world.DeleteObject(ch);
        }

        _account.SetCharSlot(charIndex, Serial.Invalid);

        // Send success + new char list
        _netState.Send(new PacketCharDeleteResult(0));
        var charNames = _account.GetCharNames(uid => _world.FindChar(uid)?.GetName());
        _netState.Send(new PacketCharList(charNames).Build());
    }

    /// <summary>Handle dye response from color picker (0x95).</summary>
    public void HandleDyeResponse(uint itemSerial, ushort hue)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(itemSerial));
        if (item == null) return;

        // Only GM can dye any item; players need a dye vat interaction (handled by script)
        if (_account?.PrivLevel < PrivLevel.GM)
        {
            SysMessage(ServerMessages.Get("itemuse_dye_fail"));
            return;
        }

        _logger.LogDebug("[dye_response] char=0x{Uid:X8} item=0x{Item:X8} hue={Hue}",
            _character.Uid.Value, itemSerial, hue);
        item.Hue = new Core.Types.Color(hue);

        // Refresh item for nearby clients
        var itemPacket = new PacketWorldItem(
            item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, item.Hue);
        BroadcastNearby?.Invoke(item.Position, UpdateRange, itemPacket, 0);
    }

    private Action<uint, uint, uint, string>? _pendingPromptCallback;
    private uint _pendingPromptId;

    /// <summary>Send a text prompt to the client and register a callback for the response.</summary>
    public void SendPrompt(uint promptId, string message, Action<uint, uint, uint, string>? callback = null)
    {
        if (_character == null) return;
        _pendingPromptId = promptId;
        _pendingPromptCallback = callback;
        _netState.Send(new PacketPromptRequest(_character.Uid.Value, promptId, message).Build());
    }

    /// <summary>Handle prompt response (0x9A) — rune names, house signs, etc.</summary>
    public void HandlePromptResponse(uint serial, uint promptId, uint type, string text)
    {
        if (_character == null) return;

        _logger.LogDebug("[prompt_response] char=0x{Uid:X8} promptId={PromptId} type={Type} text='{Text}'",
            _character.Uid.Value, promptId, type, text);

        if (type == 0)
        {
            // Cancelled
            _pendingPromptCallback = null;
            return;
        }

        // Dispatch to pending callback
        if (_pendingPromptCallback != null)
        {
            _pendingPromptCallback(serial, promptId, type, text);
            _pendingPromptCallback = null;
            return;
        }

        // Default: try to set the name of the target item (rune, house sign)
        var item = _world.FindItem(new Serial(serial));
        if (item != null && !string.IsNullOrWhiteSpace(text))
        {
            item.Name = text.Trim();
            SysMessage(ServerMessages.GetFormatted("msg_name_set", item.Name));
        }
    }

    /// <summary>Handle old-style menu choice response (0x7D).</summary>
    public void HandleMenuChoice(uint serial, ushort menuId, ushort index, ushort modelId)
    {
        if (_character == null) return;

        _logger.LogDebug("[menu_choice] char=0x{Uid:X8} serial=0x{Serial:X8} menuId={MenuId} index={Index} modelId=0x{Model:X4}",
            _character.Uid.Value, serial, menuId, index, modelId);

        if (menuId == EditMenuId)
        {
            HandleEditMenuChoice(index);
            return;
        }

        var options = _pendingMenuOptions;
        var defname = _pendingMenuDefname;
        _pendingMenuOptions = null;
        _pendingMenuDefname = "";

        if (index == 0)
        {
            // Cancel — fire @Cancel trigger if a MENU section trigger handler exists
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserExtCmd,
                new TriggerArgs { CharSrc = _character, S1 = $"menu_{defname}_cancel" });
            return;
        }

        if (options != null && index >= 1 && index <= options.Count)
        {
            var chosen = options[index - 1];
            foreach (var scriptKey in chosen.Script)
            {
                TryExecuteScriptCommand(_character, scriptKey.Key, scriptKey.Arg, null);
            }
            return;
        }

        // Fallback: generic trigger for unhandled menus
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserExtCmd,
            new TriggerArgs { CharSrc = _character, S1 = $"menu_{menuId}_{index}" });
    }

    // ==================== Phase 2: Content Feature Handlers ====================

    private void OpenBook(Item book, bool writable)
    {
        if (_character == null) return;

        string title = book.TryGetTag("BOOK_TITLE", out string? t) && t != null ? t : book.GetName();
        string author = book.TryGetTag("BOOK_AUTHOR", out string? a) && a != null ? a : "";
        ushort pageCount = 16;
        if (book.TryGetTag("BOOK_PAGES", out string? ps) && ushort.TryParse(ps, out ushort pc))
            pageCount = pc;

        _netState.Send(new PacketBookHeaderOut(
            book.Uid.Value, writable, pageCount, title, author));

        var pages = new List<(ushort PageNum, string[] Lines)>();
        for (ushort i = 1; i <= pageCount; i++)
        {
            string[] lines;
            if (book.TryGetTag($"PAGE_{i}", out string? content) && !string.IsNullOrEmpty(content))
                lines = content.Split('\n');
            else
                lines = [];
            pages.Add((i, lines));
        }

        _netState.Send(new PacketBookPageContent(
            book.Uid.Value, pages.ToArray()));
    }

    /// <summary>Handle book page read/write (0x66).</summary>
    public void HandleBookPage(uint serial, List<(ushort PageNum, string[] Lines)> pages)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        foreach (var (pageNum, lines) in pages)
        {
            if (lines.Length == 0)
            {
                // Read request — send page content back
                string[] pageLines;
                if (item.TryGetTag($"PAGE_{pageNum}", out string? content) && !string.IsNullOrEmpty(content))
                    pageLines = content.Split('\n');
                else
                    pageLines = [];

                _netState.Send(new PacketBookPageContent(
                    serial, [(pageNum, pageLines)]));
                continue;
            }

            // Write request — store page content in tags
            string pageContent = string.Join("\n", lines);
            item.SetTag($"PAGE_{pageNum}", pageContent);
        }
    }

    /// <summary>Handle book header change (0x93).</summary>
    public void HandleBookHeader(uint serial, bool writable, string title, string author)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        _logger.LogDebug("[book_header] item=0x{Item:X8} title='{Title}' author='{Author}'",
            serial, title, author);

        if (writable)
        {
            item.SetTag("BOOK_TITLE", title);
            item.SetTag("BOOK_AUTHOR", author);
        }
    }

    /// <summary>Handle bulletin board list request (0x71 sub 3).</summary>
    public void HandleBulletinBoardRequestList(uint boardSerial)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_list] char=0x{Uid:X8} board=0x{Board:X8}",
            _character.Uid.Value, boardSerial);
        // Bulletin board content is managed via scripts/TAGs
    }

    /// <summary>Handle bulletin board message read (0x71 sub 4).</summary>
    public void HandleBulletinBoardRequestMessage(uint boardSerial, uint msgSerial)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_read] board=0x{Board:X8} msg=0x{Msg:X8}", boardSerial, msgSerial);
    }

    /// <summary>Handle bulletin board post (0x71 sub 5).</summary>
    public void HandleBulletinBoardPost(uint boardSerial, uint replyTo, string subject, string[] bodyLines)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_post] board=0x{Board:X8} subject='{Subject}' lines={Lines}",
            boardSerial, subject, bodyLines.Length);
        SysMessage(ServerMessages.Get("msg_message_posted"));
    }

    /// <summary>Handle bulletin board delete (0x71 sub 6).</summary>
    public void HandleBulletinBoardDelete(uint boardSerial, uint msgSerial)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_delete] board=0x{Board:X8} msg=0x{Msg:X8}", boardSerial, msgSerial);
    }

    /// <summary>Handle map detail request (0x90).</summary>
    public void HandleMapDetail(uint serial)
    {
        if (_character == null) return;
        _logger.LogDebug("[map_detail] char=0x{Uid:X8} map=0x{Serial:X8}",
            _character.Uid.Value, serial);
        // Map detail rendering is handled client-side with MUL data
    }

    /// <summary>Handle map pin edit (0x56).</summary>
    public void HandleMapPinEdit(uint serial, byte action, byte pinId, ushort x, ushort y)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        _logger.LogDebug("[map_pin] item=0x{Item:X8} action={Action} pin={PinId} x={X} y={Y}",
            serial, action, pinId, x, y);

        switch (action)
        {
            case 1: // Add pin
                item.SetTag($"PIN_{pinId}", $"{x},{y}");
                break;
            case 6: // Insert pin
                item.SetTag($"PIN_{pinId}", $"{x},{y}");
                break;
            case 7: // Move pin
                item.SetTag($"PIN_{pinId}", $"{x},{y}");
                break;
        }
    }

    // ==================== Phase 3: Client Compatibility Handlers ====================

    /// <summary>Handle 0xAC Gump Value Input reply (response to a 0xAB
    /// dialog opened by the Source-X <c>INPDLG</c> verb). Looks up the
    /// pending <c>(serial, context)</c> entry stored when the prompt was
    /// sent and writes <paramref name="text"/> into the named property
    /// on the target object via <c>TrySetProperty</c>.</summary>
    public void HandleGumpTextEntry(uint serial, ushort context, byte action, string text)
    {
        if (_character == null) return;

        var key = (serial, context);
        if (!_pendingInputDlg.TryGetValue(key, out var propName))
        {
            _logger.LogDebug("[inpdlg] unexpected text input: serial=0x{S:X8} ctx=0x{C:X4}", serial, context);
            return;
        }
        _pendingInputDlg.Remove(key);

        if (action == 0)
        {
            _logger.LogDebug("[inpdlg] cancelled by user (serial=0x{S:X8} prop={P})", serial, propName);
            return;
        }

        IScriptObj? target = _world.FindChar(new Serial(serial)) as IScriptObj
            ?? _world.FindItem(new Serial(serial)) as IScriptObj;
        if (target == null)
        {
            _logger.LogDebug("[inpdlg] target serial 0x{S:X8} no longer exists", serial);
            return;
        }

        // Source-X parity: a single "#" means "default value" — currently
        // we just clear the property (TrySetProperty empty arg).
        string value = text == "#" ? "" : text;

        var posBefore = (target as Character)?.Position;
        ushort bodyBefore = (target as Character)?.BodyId ?? 0;
        ushort hueBefore = (target as Character)?.Hue.Value ?? 0;

        if (!target.TrySetProperty(propName, value))
        {
            // Source-X falls back to executing the verb if it isn't a
            // straight property — handles "INPDLG ANIM 30" style edits.
            target.TryExecuteCommand(propName, value, this);
        }

        if (target is Character ch)
        {
            bool moved = posBefore.HasValue && !ch.Position.Equals(posBefore.Value);
            bool appearance = ch.BodyId != bodyBefore || ch.Hue.Value != hueBefore;
            if (moved)
            {
                _world.MoveCharacter(ch, ch.Position);
                if (ch == _character)
                    Resync();
                BroadcastDrawObject(ch);
            }
            else if (appearance)
            {
                BroadcastDrawObject(ch);
            }
        }
    }

    /// <summary>
    /// Open a Source-X style <c>INPDLG</c> input prompt on this client.
    /// The user types a value into a small text-entry gump; on submit,
    /// <see cref="HandleGumpTextEntry"/> writes that value into
    /// <paramref name="propName"/> on <paramref name="target"/>.
    /// </summary>
    public void SendInputPromptGump(IScriptObj target, string propName, int maxLength)
    {
        if (target == null || string.IsNullOrWhiteSpace(propName))
            return;

        uint targetSerial = 0;
        if (target is Character ch) targetSerial = ch.Uid.Value;
        else if (target is Item it) targetSerial = it.Uid.Value;
        else return;

        ushort context = unchecked(_nextInputDlgContext++);
        if (_nextInputDlgContext == 0)
            _nextInputDlgContext = 0x1000;

        _pendingInputDlg[(targetSerial, context)] = propName;

        string current = ".";
        if (target.TryGetProperty(propName, out var cur) && !string.IsNullOrEmpty(cur))
            current = cur;

        string caption = $"{propName} (# = default)";
        string description = string.IsNullOrEmpty(current) ? "." : current;

        var packet = new PacketGumpValueInput(
            targetSerial,
            context,
            caption,
            description,
            (uint)Math.Max(1, maxLength),
            PacketGumpValueInput.InputStyle.TextEdit,
            cancel: true);
        _netState.Send(packet);
    }

    /// <summary>Handle all names request (0x98).</summary>
    public void HandleAllNamesRequest(uint serial)
    {
        if (_character == null) return;

        var ch = _world.FindChar(new Serial(serial));
        if (ch != null)
        {
            _netState.Send(new PacketAllNamesResponse(serial, ch.GetName()).Build());
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item != null)
        {
            _netState.Send(new PacketAllNamesResponse(serial, item.GetName()).Build());
        }
    }

    // ==================== Helpers ====================

    private static void GetDirectionDelta(Direction dir, out short dx, out short dy)
    {
        dx = 0; dy = 0;
        switch (dir)
        {
            case Direction.North: dy = -1; break;
            case Direction.NorthEast: dx = 1; dy = -1; break;
            case Direction.East: dx = 1; break;
            case Direction.SouthEast: dx = 1; dy = 1; break;
            case Direction.South: dy = 1; break;
            case Direction.SouthWest: dx = -1; dy = 1; break;
            case Direction.West: dx = -1; break;
            case Direction.NorthWest: dx = -1; dy = -1; break;
        }
    }
}
