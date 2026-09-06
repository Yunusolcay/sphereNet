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
            case 1:
            case 2:
                // Source-X treats every non-zero response as "play as ghost".
                // Resurrection must use the healer/shrine/spell pipeline.
                if (!_character.IsDead)
                    break;
                _logger.LogDebug("[death_menu] char=0x{Uid:X8} continues as ghost", _character.Uid.Value);
                SysMessage("You are a ghost");
                _netState.Send(new PacketSound(0x017F, _character.X, _character.Y, _character.Z));
                Resync();
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

            // Source-X Setup_Delete (CClientMsg.cpp:2961): a character younger
            // than MINCHARDELETETIME cannot be deleted; Counsel+ accounts
            // bypass. 0x85 reason 3 = "character is not old enough".
            // CreatedUtcSeconds == 0 (legacy save, pre-stamp) counts as old.
            if (ServerMinCharDeleteDays > 0 && ch.CreatedUtcSeconds > 0 &&
                _account.PrivLevel < PrivLevel.Counsel)
            {
                long ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ch.CreatedUtcSeconds;
                if (ageSeconds < ServerMinCharDeleteDays * 86400L)
                {
                    _netState.Send(new PacketCharDeleteResult(3)); // 3=not old enough
                    return;
                }
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
        int maxChars = GetEffectiveMaxChars();
        var res = ResolveAccountResDisplay();
        uint flags = BuildCharacterListFlags(res, maxChars, ServerToolTipMode != 0);
        _netState.Send(new PacketCharList(charNames, maxChars,
            _netState.SupportsNewCharacterList, flags).Build());
    }

    /// <summary>Re-send the character selection list (Source-X CV_CHARLIST /
    /// PacketChangeCharacter 0x81 equivalent). The classic client swaps back
    /// to the char-select screen.</summary>
    internal void ResendCharacterList()
    {
        if (_account == null) return;
        var charNames = _account.GetCharNames(uid => _world.FindChar(uid)?.GetName());
        int maxChars = GetEffectiveMaxChars();
        var res = ResolveAccountResDisplay();
        uint flags = BuildCharacterListFlags(res, maxChars, ServerToolTipMode != 0);
        _netState.Send(new PacketCharList(charNames, maxChars,
            _netState.SupportsNewCharacterList, flags).Build());
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
        if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Dye, new TriggerArgs
        {
            CharSrc = _character,
            ItemSrc = item,
            N1 = hue
        }) == TriggerResult.True)
            return;

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
                // Console verbs first (SUMMON/MAKEITEM/SKILLMENU...), then the
                // character's own property/verb surface (POLY, flags, ...).
                if (TryExecuteScriptCommand(_character, scriptKey.Key, scriptKey.Arg, null))
                    continue;
                if (_character.TrySetProperty(scriptKey.Key, scriptKey.Arg))
                    continue;
                _character.TryExecuteCommand(scriptKey.Key, scriptKey.Arg, this);
            }
            return;
        }

        // Fallback: generic trigger for unhandled menus
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserExtCmd,
            new TriggerArgs { CharSrc = _character, S1 = $"menu_{menuId}_{index}" });
    }

    // ==================== Phase 2: Content Feature Handlers ====================

    internal void OpenBook(Item book, bool writable)
    {
        if (_character == null) return;

        string title = book.TryGetTag("BOOK_TITLE", out string? t) && t != null ? t : book.GetName();
        string author = book.TryGetTag("BOOK_AUTHOR", out string? a) && a != null ? a : "";
        // Clamp the page count and iterate with an int. A BOOK_PAGES tag of 65535
        // (corrupt/hand-edited save) drove the old "for (ushort i = 1; i <= pageCount)"
        // past 65535, wrapped i to 0, and looped forever on the main thread (E1).
        // A writable book offers the whole writing capacity, a finished one offers
        // exactly the pages it has (PacketDisplayBook, send.cpp:2919). Opening with a
        // flat 16 while the page handler happily stored page 17..64 meant the text was
        // in the book but outside the range the window ever asked for. BOOK_PAGES, when
        // the script sets it, still stands in for the resource's own page count.
        int pageCount = writable ? MaxBookPages : CountWrittenPages(book);
        // BOOK_PAGES stands in for a system book's resource PAGES, which upstream reads
        // straight out of the resource with no MAX_BOOK_PAGES clamp (send.cpp:2901).
        // The 256 ceiling here is not parity, it is the E1 anti-hang guard on a
        // hand-edited count.
        if (book.TryGetTag("BOOK_PAGES", out string? ps) && int.TryParse(ps, out int pc))
            pageCount = Math.Clamp(pc, 0, DeclaredPageHardCap);

        _netState.Send(new PacketBookHeaderOut(
            book.Uid.Value, writable, (ushort)pageCount, title, author));

        var pages = new List<(ushort PageNum, string[] Lines)>();
        for (int i = 1; i <= pageCount; i++)
        {
            string[] lines;
            if (book.TryGetTag($"PAGE_{i}", out string? content) && !string.IsNullOrEmpty(content))
                lines = content.Split('\n');
            else
                lines = [];
            pages.Add(((ushort)i, lines));
        }

        SendBookPages(book.Uid.Value, pages);
    }

    /// <summary>Source-X MAX_BOOK_PAGES (sphereproto.h:767) - the hard ceiling on a
    /// user-written book, applied both to what is offered and to what is accepted.</summary>
    private const int MaxBookPages = 64;

    /// <summary>Ceiling on a script- or save-declared BOOK_PAGES. SphereNet-only: a
    /// corrupt 65535 used to drive the open loop past a ushort and spin forever (E1).</summary>
    private const int DeclaredPageHardCap = 256;

    /// <summary>How many pages the book actually holds, counting from the first.</summary>
    private static int CountWrittenPages(Item book)
    {
        int pages = 0;
        while (pages < MaxBookPages &&
               !string.IsNullOrEmpty(book.Tags.Get($"PAGE_{pages + 1}")))
            pages++;
        return pages;
    }

    /// <summary>Is this actually a book? Upstream every editing entry casts to
    /// CItemMessage first and gives up when the cast fails
    /// (receive.cpp:1017, CClientEvent.cpp:159).</summary>
    private static bool IsBookItem(Item item) =>
        item.ItemType is ItemType.Book or ItemType.Message;

    /// <summary>Send book pages as one or more 0x66 packets, splitting so no
    /// single packet approaches the 65535-byte variable-length ceiling. A normal
    /// book fits one packet (unchanged wire behaviour); an oversized/poisoned book
    /// is split, and any residual over-budget single page is caught by the send
    /// path's oversize guard rather than emitting a wrapped length field.</summary>
    private void SendBookPages(uint serial, IReadOnlyList<(ushort PageNum, string[] Lines)> pages)
    {
        const int Budget = 60000; // headroom under 65535 for the packet header
        var group = new List<(ushort, string[])>();
        int size = 8; // opcode(1)+len(2)+serial(4)+pageCount(2), rounded up
        foreach (var page in pages)
        {
            int pageSize = 4; // pageNum(2) + lineCount(2)
            foreach (var line in page.Lines)
                pageSize += System.Text.Encoding.ASCII.GetByteCount(line) + 1; // + NUL
            if (group.Count > 0 && size + pageSize > Budget)
            {
                _netState.Send(new PacketBookPageContent(serial, group.ToArray()));
                group.Clear();
                size = 8;
            }
            group.Add(page);
            size += pageSize;
        }
        if (group.Count > 0)
            _netState.Send(new PacketBookPageContent(serial, group.ToArray()));
    }

    /// <summary>Handle book page read/write (0x66).</summary>
    public void HandleBookPage(uint serial, List<(ushort PageNum, string[] Lines)> pages)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;
        // Reading a page needs to be able to SEE the book (receive.cpp:1002); the
        // handler used to work on any item at any distance, so a window left open
        // after the book was carried off still read and wrote it - and the same call
        // wrote PAGE_n tags onto a pile of gold.
        if (!ItemUse.CanSeeItem(item)) return;

        bool canWrite = IsBookItem(item) &&
            (_character.PrivLevel >= PrivLevel.GM || IsBookWritableBy(item, _character));

        int maxPages = MaxBookPages;

        foreach (var (pageNum, lines) in pages)
        {
            if (pageNum < 1 || pageNum > maxPages)
                continue;

            if (lines.Length == 0)
            {
                // Read request — send page content back
                string[] pageLines;
                if (item.TryGetTag($"PAGE_{pageNum}", out string? content) && !string.IsNullOrEmpty(content))
                    pageLines = content.Split('\n');
                else
                    pageLines = [];

                SendBookPages(serial, [(pageNum, pageLines)]);
                continue;
            }

            if (!canWrite)
                continue;

            // Write request — store page content in tags
            string pageContent = string.Join("\n", lines);
            item.SetTag($"PAGE_{pageNum}", pageContent);
        }
    }

    private bool IsBookWritableBy(Item book, Character ch)
    {
        if (book.TryGetTag("BOOK_WRITABLE", out string? w) && w == "0")
            return false;
        if (!book.ContainedIn.IsValid)
            return true;
        var current = book;
        for (int depth = 0; depth < 16 && current.ContainedIn.IsValid; depth++)
        {
            if (current.ContainedIn == ch.Uid)
                return true;
            var parent = _world.FindItem(current.ContainedIn);
            if (parent == null) break;
            current = parent;
        }
        return false;
    }

    /// <summary>Handle book header change (0x93).</summary>
    public void HandleBookHeader(uint serial, bool writable, string title, string author)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null || !IsBookItem(item)) return;

        _logger.LogDebug("[book_header] item=0x{Item:X8} title='{Title}' author='{Author}'",
            serial, title, author);

        // Retitling needs REACH, not just sight (Event_Book_Title, CClientEvent.cpp:165).
        if (!ItemUse.CanTouchItem(item))
        {
            SysMessage(ServerMessages.Get(Msg.ReachFail));
            return;
        }

        if (writable && (_character.PrivLevel >= PrivLevel.GM || IsBookWritableBy(item, _character)))
        {
            item.SetTag("BOOK_TITLE", title);
            item.SetTag("BOOK_AUTHOR", author);
            // The book's title is its name upstream (SetName, :180).
            item.Name = title;
        }
    }

    // Bulletin board (Source-X CItemMessage model): each posted message is a
    // child item inside the board container — Name = subject, LINK = author
    // char, tags AUTHOR / TIME / BODY_n hold the display fields.
    private const int MaxBoardMessages = 32;
    private const ushort BoardMessageGraphic = 0x0EB0; // rolled-up scroll

    private Item? ResolveBoardMessage(uint boardSerial, uint msgSerial, out Item? board)
    {
        board = _world.FindItem(new Serial(boardSerial));
        if (board == null || board.ItemType != ItemType.BBoard) return null;
        // Every sub-command of the board packet is gated on being able to see the
        // board (receive.cpp:1193). Without it a window left open after walking away
        // still read messages and deleted the player's own - the author check was the
        // only thing left standing.
        if (!ItemUse.CanSeeItem(board)) { board = null; return null; }
        var msg = _world.FindItem(new Serial(msgSerial));
        return msg != null && msg.ContainedIn == board.Uid ? msg : null;
    }

    private static string[] ReadBoardBody(Item msg)
    {
        var lines = new List<string>();
        for (int i = 1; i <= 32; i++)
        {
            string? line = msg.Tags.Get($"BODY_{i}");
            if (line == null) break;
            lines.Add(line);
        }
        return lines.Count > 0 ? lines.ToArray() : [""];
    }

    /// <summary>Handle bulletin board header request (0x71 sub 3, Source-X
    /// BBOARDF_REQ_HEAD): reply with the message summary (sub 1).</summary>
    public void HandleBulletinBoardRequestHead(uint boardSerial, uint msgSerial)
    {
        if (_character == null) return;
        var msg = ResolveBoardMessage(boardSerial, msgSerial, out _);
        if (msg == null) return;
        _netState.Send(new PacketBulletinBoardOut(boardSerial, msgSerial,
            msg.Tags.Get("AUTHOR") ?? "", msg.Name ?? "", msg.Tags.Get("TIME") ?? "", null));
    }

    /// <summary>Handle bulletin board message read (0x71 sub 4, BBOARDF_REQ_FULL):
    /// reply with the full message body (sub 2).</summary>
    public void HandleBulletinBoardRequestMessage(uint boardSerial, uint msgSerial)
    {
        if (_character == null) return;
        var msg = ResolveBoardMessage(boardSerial, msgSerial, out _);
        if (msg == null) return;
        _netState.Send(new PacketBulletinBoardOut(boardSerial, msgSerial,
            msg.Tags.Get("AUTHOR") ?? "", msg.Name ?? "", msg.Tags.Get("TIME") ?? "",
            ReadBoardBody(msg)));
    }

    /// <summary>Handle bulletin board post (0x71 sub 5): create the message
    /// item inside the board; the oldest message rolls off past the cap.</summary>
    public void HandleBulletinBoardPost(uint boardSerial, uint replyTo, string subject, string[] bodyLines)
    {
        if (_character == null) return;
        var board = _world.FindItem(new Serial(boardSerial));
        if (board == null || board.ItemType != ItemType.BBoard) return;
        if (_character.Position.GetDistanceTo(board.GetTopLevelPosition()) > 4)
        {
            SysMessage("You can't reach the board.");
            return;
        }

        if (board.Contents.Count >= MaxBoardMessages)
        {
            var oldest = board.Contents[0];
            _world.RemoveItem(oldest);
        }

        var msg = _world.CreateItem();
        msg.BaseId = BoardMessageGraphic;
        // A posted message is fixed in place (receive.cpp:1252). The pickup path
        // refused it for other reasons, but every rule that asks the object itself
        // was being told the notice could be carried away.
        msg.SetAttr(ObjAttributes.Move_Never);
        msg.Name = string.IsNullOrEmpty(subject) ? "(no subject)" : subject;
        msg.Link = _character.Uid;
        msg.SetTag("AUTHOR", _character.Name ?? "");
        msg.SetTag("TIME", DateTime.UtcNow.ToString("MMM d, yyyy",
            System.Globalization.CultureInfo.InvariantCulture));
        if (replyTo != 0)
            msg.SetTag("REPLYTO", $"0{replyTo:X}");
        for (int i = 0; i < bodyLines.Length && i < 32; i++)
            msg.SetTag($"BODY_{i + 1}", bodyLines[i]);
        board.AddItem(msg);

        // The client learns about the new message through a container-item
        // update, then requests its header.
        _netState.Send(new PacketContainerItem(
            msg.Uid.Value, msg.DispIdFull, 0, 1, 0, 0,
            board.Uid.Value, msg.Hue, _netState.IsClientPost6017));
        SysMessage(ServerMessages.Get("msg_message_posted"));
    }

    /// <summary>Handle bulletin board delete (0x71 sub 6): only the author
    /// or a GM may remove a message.</summary>
    public void HandleBulletinBoardDelete(uint boardSerial, uint msgSerial)
    {
        if (_character == null) return;
        var msg = ResolveBoardMessage(boardSerial, msgSerial, out var board);
        if (msg == null || board == null) return;
        if (msg.Link != _character.Uid && _character.PrivLevel < PrivLevel.GM)
        {
            SysMessage("That is not your message.");
            return;
        }
        _world.RemoveItem(msg);
        _netState.Send(new PacketDeleteObject(msgSerial));
    }

    /// <summary>Handle map detail request (0x90).</summary>
    public void HandleMapDetail(uint serial)
    {
        if (_character == null) return;
        _logger.LogDebug("[map_detail] char=0x{Uid:X8} map=0x{Serial:X8}",
            _character.Uid.Value, serial);
        // Map detail rendering is handled client-side with MUL data
    }

    /// <summary>Handle map pin edit (0x56). Source-X MAPCMD semantics:
    /// 1 add (append), 2 insert at index, 3 move, 4 delete, 5 clear all,
    /// 6 toggle plot mode (server replies mode 7 with the new state).
    /// Pins live as 1-based PIN_n tags (the script PIN.n surface); the
    /// client's pin index is 0-based.</summary>
    public void HandleMapPinEdit(uint serial, byte action, byte pinId, ushort x, ushort y)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        _logger.LogDebug("[map_pin] item=0x{Item:X8} action={Action} pin={PinId} x={X} y={Y}",
            serial, action, pinId, x, y);

        // It has to be a map, and it has to be within reach (PacketMapEdit,
        // receive.cpp:867). Existence alone let a stale window pin a map across the
        // world, and let the same tags be written onto whatever else the serial named.
        if (item.ItemType is not (ItemType.Map or ItemType.MapBlank) ||
            !ItemUse.CanTouchItem(item))
        {
            SysMessage(ServerMessages.Get(Msg.ReachFail));
            return;
        }

        // MOREZ glues the pins down (CItem.h:295). Everyone is told; only staff may
        // then go on and change them (:875).
        if (item.MoreP.Z != 0)
        {
            SysMessage("The pins seem to be glued in place.");
            if (_character.PrivLevel < PrivLevel.GM)
                return;
        }

        const int maxPins = 128;
        int count = 0;
        while (count < maxPins && !string.IsNullOrEmpty(item.Tags.Get($"PIN_{count + 1}")))
            count++;

        switch (action)
        {
            case 1: // add — append to the end
                if (count < maxPins)
                    item.SetTag($"PIN_{count + 1}", $"{x},{y}");
                break;
            case 2: // insert between two pins — shift the tail up
            {
                if (count >= maxPins || pinId > count) break;
                for (int i = count; i > pinId; i--)
                    item.SetTag($"PIN_{i + 1}", item.Tags.Get($"PIN_{i}") ?? "");
                item.SetTag($"PIN_{pinId + 1}", $"{x},{y}");
                break;
            }
            case 3: // move
                if (pinId < count)
                    item.SetTag($"PIN_{pinId + 1}", $"{x},{y}");
                break;
            case 4: // delete — shift the tail down
            {
                if (pinId >= count) break;
                for (int i = pinId + 1; i < count; i++)
                    item.SetTag($"PIN_{i}", item.Tags.Get($"PIN_{i + 1}") ?? "");
                item.RemoveTag($"PIN_{count}");
                break;
            }
            case 5: // clear all pins
                for (int i = 1; i <= count; i++)
                    item.RemoveTag($"PIN_{i}");
                break;
            case 6: // toggle plot mode — reply MAP_SENT with the new state
            {
                bool plot = item.Tags.Get("PLOTMODE") == "1";
                bool newPlot = !plot;
                if (newPlot) item.SetTag("PLOTMODE", "1");
                else item.RemoveTag("PLOTMODE");
                _netState.Send(new PacketMapPlot(serial, 7, newPlot));
                break;
            }
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
        if (!Dialogs.PendingInputDlg.TryGetValue(key, out var propName))
        {
            _logger.LogDebug("[inpdlg] unexpected text input: serial=0x{S:X8} ctx=0x{C:X4}", serial, context);
            return;
        }
        Dialogs.PendingInputDlg.Remove(key);

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

        byte? speedModeBefore = target is Character targetChar ? targetChar.SpeedMode : null;
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
                if (ch == _character)
                    SendSelfRedraw();
                else
                    BroadcastDrawObject(ch);
            }

            if (ch == _character && speedModeBefore.HasValue && ch.SpeedMode != speedModeBefore.Value)
                SendSpeedMode();
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

        ushort context = unchecked(Dialogs.NextInputDlgContext++);
        if (Dialogs.NextInputDlgContext == 0)
            Dialogs.NextInputDlgContext = 0x1000;

        Dialogs.PendingInputDlg[(targetSerial, context)] = propName;

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

    internal static void GetDirectionDelta(Direction dir, out short dx, out short dy)
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
