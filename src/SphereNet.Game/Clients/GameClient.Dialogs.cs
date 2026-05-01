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
    private void ShowHelpPageDialog(int requestedPage)
    {
        if (_character == null)
            return;

        int page = Math.Clamp(requestedPage, 1, 4);
        _character.SetTag("help_type", page.ToString());

        string[] menu = ["Genel", "Yardim", "Stuck", "Istatistik"];

        var gump = new GumpBuilder(_character.Uid.Value, (uint)Math.Abs("d_helppage".GetHashCode()), 500, 360);
        gump.AddResizePic(0, 0, 5054, 500, 360)
            .AddResizePic(15, 15, 2620, 130, 300)
            .AddResizePic(155, 15, 2620, 330, 300)
            .AddText(30, 25, 0x0481, "Help")
            .AddText(175, 25, 0x0481, "Bilgi");

        for (int i = 0; i < menu.Length; i++)
        {
            int idx = i + 1;
            int y = 65 + (i * 42);
            gump.AddButton(28, y, 4005, 4007, idx)
                .AddText(62, y + 2, idx == page ? (ushort)0x0021 : (ushort)0x0481, menu[i]);
        }

        string pageTitle = menu[page - 1];
        gump.AddText(175, 60, 0x0481, pageTitle);

        switch (page)
        {
            case 1:
                gump.AddHtmlGump(175, 90, 280, 160,
                    "Genel yardim menusu.<br><br>Detayli sistemler daha sonra script tarafindan doldurulabilir.",
                    true, true);
                break;
            case 2:
                gump.AddHtmlGump(175, 90, 280, 120,
                    "Sorunun varsa staff'a page atabilir veya mevcut page durumunu kontrol edebilirsin.",
                    true, true)
                    .AddButton(175, 235, 4005, 4007, 21)
                    .AddText(210, 237, 0x0481, "Page")
                    .AddButton(300, 235, 4005, 4007, 22)
                    .AddText(335, 237, 0x0481, "Page List");
                break;
            case 3:
                gump.AddHtmlGump(175, 90, 280, 120,
                    "Karakterin takildiysa uygun bir guvenli nokta secerek cikabilirsin.",
                    true, true)
                    .AddButton(175, 235, 4005, 4007, 30)
                    .AddText(210, 237, 0x0481, "Town")
                    .AddButton(300, 235, 4005, 4007, 31)
                    .AddText(335, 237, 0x0481, "Inn");
                break;
            case 4:
            {
                var stats = _world.GetStats();
                gump.AddHtmlGump(175, 90, 280, 160,
                    $"Online Oyuncu: {_world.GetAllObjects().OfType<Character>().Count(c => c.IsPlayer && c.IsOnline)}<br>" +
                    $"Yaratik Sayisi: {stats.Chars}<br>" +
                    $"Esya Sayisi: {stats.Items}<br>" +
                    $"Sektor Sayisi: {stats.Sectors}",
                    true, true);
                break;
            }
        }

        gump.AddButton(455, 22, 4017, 4019, 0);

        SendGump(gump, (buttonId, _, _) =>
        {
            if (_character == null)
                return;
            if (buttonId == 0)
                return;

            if (buttonId is >= 1 and <= 4)
            {
                ShowHelpPageDialog((int)buttonId);
                return;
            }

            if (buttonId is >= 30 and <= 31)
            {
                SysMessage(ServerMessages.Get("msg_stuck_script"));
                return;
            }

            if (buttonId == 21)
            {
                SysMessage(ServerMessages.Get("msg_page_script"));
                return;
            }

            if (buttonId == 22)
            {
                SysMessage(ServerMessages.Get("msg_pagelist_script"));
            }
        });
    }

    /// <summary>Open a script-defined dialog ([DIALOG &lt;name&gt;] sections)
    /// on this client. Returns false when the dialog name cannot be
    /// resolved — caller logs or sysmessages accordingly. Public so
    /// admin commands (".dialog") and script-command handlers share the
    /// same code path.</summary>
    public bool TryShowScriptDialog(string dialogId, int requestedPage)
        => TryShowScriptDialog(dialogId, requestedPage, subject: null);

    /// <summary>Open a script DIALOG section. When <paramref name="subject"/>
    /// is non-null, bare property reads inside the dialog resolve against
    /// that object first (Source-X CLIMODE_DIALOG pObj semantics) — needed
    /// by d_charprop1 / d_itemprop1 where the gump is bound to an inspected
    /// target instead of the GM.</summary>
    public bool TryShowScriptDialog(string dialogId, int requestedPage, ObjBase? subject)
    {
        if (_character == null || _commands?.Resources == null)
            return false;

        if (!TryFindDialogSections(dialogId, out var layoutSection))
            return false;

        var textLines = _commands.Resources.GetDialogTextLines(dialogId);

        var prevSubject = _dialogSubjectUid;
        _dialogSubjectUid = subject?.Uid ?? Serial.Invalid;
        try
        {
            return RenderScriptDialog(dialogId, requestedPage, layoutSection, subject?.Uid ?? Serial.Invalid, textLines);
        }
        finally
        {
            _dialogSubjectUid = prevSubject;
        }
    }

    private bool RenderScriptDialog(string dialogId, int requestedPage,
        SphereNet.Scripting.Parsing.ScriptSection layoutSection, Serial subjectUid,
        List<string>? textLines = null)
    {
        if (_character == null) return false;

        int currentPage = Math.Max(0, requestedPage);

        // Sphere dialog first line is the screen position "x,y".
        // Source-X reads this via s.ReadKey() before processing controls.
        int dialogX = 0, dialogY = 0;
        if (layoutSection.Keys.Count > 0)
        {
            string firstLine = layoutSection.Keys[0].Key.Trim();
            var posParts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (posParts.Length >= 2 && int.TryParse(posParts[0], out int px) && int.TryParse(posParts[1], out int py))
            {
                dialogX = px;
                dialogY = py;
            }
        }

        var gump = new GumpBuilder(_character.Uid.Value, (uint)Math.Abs(dialogId.GetHashCode()))
        {
            ExplicitX = dialogX,
            ExplicitY = dialogY
        };
        int originX = 0, originY = 0;
        int cursorX = 0, cursorY = 0;
        // Separate "row tracker" for the `*N` operator. Sphere treats *N as a
        // fresh row step independent of the +/- cursor used for column work.
        int rowCursorX = 0, rowCursorY = 0;
        // Sphere/UO page semantics: content emitted before the first PAGE
        // marker belongs to page 0 (shared/common) and must render
        // immediately. Some imported dialogs (e.g. d_admin_player_tweak)
        // never declare an explicit PAGE 0 header, so starting hidden would
        // drop the entire layout and produce an almost-empty 0xDD packet.
        bool currentPageVisible = true;

        // Per-call local variable scope for LOCAL.x= assignments and
        // <local.x> / <dlocal.x> references — used by Sphere dialog
        // scripts that loop over a list (FOR) and emit a row per
        // iteration. Resolvers below look here first before delegating.
        var dialogLocals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Expand FOR / WHILE / IF blocks into a flat key sequence so the
        // render switch below can remain a linear walk. Each unrolled
        // copy of a loop body runs with the iterator's value substituted
        // into <local._for> / <local.n> / etc. before render commands see
        // the args — matching Sphere's runtime-expansion behaviour.
        var expandedKeys = ExpandDialogScriptKeys(layoutSection.Keys, dialogLocals, requestedPage);

        // Diagnostic: count of commands per page post-expansion. If page 4
        // (FLAGS) comes out empty while the others are populated, the
        // FOR/IF expansion isn't unrolling into output.
        {
            int currentP = 0;
            var perPage = new Dictionary<int, int>();
            foreach (var k in expandedKeys)
            {
                string ck = k.Key.Trim().ToUpperInvariant();
                if (ck == "PAGE" && int.TryParse(k.Arg.Trim(), out int np))
                {
                    currentP = np;
                    continue;
                }
                perPage[currentP] = perPage.GetValueOrDefault(currentP) + 1;
            }
            _logger.LogDebug("[dialog_expand] id={Id} keys={Total} pages={Pages}",
                dialogId, expandedKeys.Count,
                string.Join(", ", perPage.Select(kv => $"p{kv.Key}:{kv.Value}")));
        }

        foreach (var key in expandedKeys)
        {
            string cmd = key.Key.Trim().ToUpperInvariant();
            string args = key.Arg;
            switch (cmd)
            {
                case "NOMOVE":
                    gump.SetNoMove();
                    break;
                case "NOCLOSE":
                    gump.SetNoClose();
                    break;
                case "NODISPOSE":
                    gump.SetNoDispose();
                    break;
                case "PAGE":
                {
                    // UO page model is CLIENT-side: every page element
                    // lives in the same gump packet, the client switches
                    // visibility when a page-nav button fires. Emit a
                    // `{ page N }` marker and let every subsequent
                    // element render under that tag.
                    // Source-X does NOT reset m_iOriginX/m_iOriginY on
                    // PAGE — the DORIGIN baseline persists across pages
                    // so that PAGE 1 content can use +N offsets relative
                    // to the last DORIGIN set on PAGE 0.
                    int pageNo = ParseIntToken(args);
                    gump.SetPage(pageNo);
                    currentPageVisible = true;
                    break;
                }
                case "DORIGIN":
                {
                    var parts = SplitTokens(args, 2);
                    if (parts.Length >= 2)
                    {
                        // Sphere semantics: DORIGIN seeds the coordinate
                        // baseline for subsequent +/* resolution; commands
                        // that follow are already expressed in dialog-space.
                        // Adding originX/originY again at emit-time causes a
                        // double offset (notably d_spawn's right-side groups
                        // jump far to the right). Keep the runtime origin at
                        // zero and move the baseline cursors instead.
                        originX = 0;
                        originY = 0;
                        cursorX = ParseIntToken(parts[0]);
                        cursorY = ParseIntToken(parts[1]);
                        rowCursorX = cursorX;
                        rowCursorY = cursorY;
                    }
                    break;
                }
                case "RESIZE":
                case "RESIZEPIC":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 5);
                    if (parts.Length >= 5)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddResizePic(x, y, ParseIntToken(parts[2]), ParseIntToken(parts[3]), ParseIntToken(parts[4]));
                    }
                    else if (cmd == "RESIZE" && parts.Length == 4)
                    {
                        // Sphere RESIZE shorthand: x,y,width,height (no gumpId)
                        // Uses default background gump 9200.
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddResizePic(x, y, 9200, ParseIntToken(parts[2]), ParseIntToken(parts[3]));
                    }
                    break;
                }
                case "GUMPIC":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 3)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int gumpId = ParseIntToken(parts[2]);
                        int hue = parts.Length >= 4 ? ParseIntToken(parts[3]) : 0;
                        gump.AddGumpPic(x, y, gumpId, hue);
                    }
                    break;
                }
                case "GUMPPICTILED":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 5);
                    if (parts.Length >= 5)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddGumpPicTiled(x, y, ParseIntToken(parts[2]), ParseIntToken(parts[3]), ParseIntToken(parts[4]));
                    }
                    break;
                }
                case "BUTTON":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddButton(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[6]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]));
                    }
                    break;
                }
                case "DHTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 6, keepRemainder: true);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        string html = ResolveDialogHtml(parts[6], _character);
                        gump.AddHtmlGump(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            html,
                            ParseIntToken(parts[4]) != 0,
                            ParseIntToken(parts[5]) != 0);
                    }
                    break;
                }
                case "HTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    // HTMLGUMP x y w h textIndex hasBackground hasScrollbar
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[4]);
                        string html = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            html = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddHtmlGump(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            html,
                            ParseIntToken(parts[5]) != 0,
                            ParseIntToken(parts[6]) != 0);
                    }
                    break;
                }
                case "DCROPPEDTEXT":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 5, keepRemainder: true);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddCroppedText(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ResolveDialogHtml(parts[5], _character));
                    }
                    break;
                }
                case "CROPPEDTEXT":
                {
                    if (!currentPageVisible) break;
                    // CROPPEDTEXT x y w h hue textIndex
                    var parts = SplitTokens(args, 6);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[5]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddCroppedText(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            txt);
                    }
                    break;
                }
                case "DTEXT":
                {
                    if (!currentPageVisible) break;
                    // DTEXT x y hue text...
                    var parts = SplitTokens(args, 3, keepRemainder: true);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddText(x, y, ParseIntToken(parts[2]),
                            ResolveDialogHtml(parts[3], _character));
                    }
                    break;
                }
                case "TEXT":
                {
                    if (!currentPageVisible) break;
                    // TEXT x y hue textIndex — index into [dialog NAME text] section
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int hue = ParseIntToken(parts[2]);
                        int idx = ParseIntToken(parts[3]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddText(x, y, hue, txt);
                    }
                    break;
                }
                case "CHECKERTRANS":
                {
                    if (!currentPageVisible) break;
                    // CHECKERTRANS x y w h
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddCheckerTrans(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]));
                    }
                    break;
                }
                case "CHECKBOX":
                {
                    if (!currentPageVisible) break;
                    // CHECKBOX x y uncheckedGumpId checkedGumpId initialState switchId
                    var parts = SplitTokens(args, 6);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddCheckbox(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]) != 0,
                            ParseIntToken(parts[5]));
                    }
                    break;
                }
                case "RADIO":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 6);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddRadio(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]) != 0,
                            ParseIntToken(parts[5]));
                    }
                    break;
                }
                case "DTEXTENTRY":
                {
                    if (!currentPageVisible) break;
                    // DTEXTENTRY x y w h hue entryId initialText...
                    var parts = SplitTokens(args, 6, keepRemainder: true);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddTextEntry(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            ResolveDialogHtml(parts[6], _character));
                    }
                    break;
                }
                case "TEXTENTRY":
                {
                    if (!currentPageVisible) break;
                    // TEXTENTRY x y w h hue entryId textIndex
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[6]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddTextEntry(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            txt);
                    }
                    break;
                }
                case "DTEXTENTRYLIMITED":
                {
                    if (!currentPageVisible) break;
                    // DTEXTENTRYLIMITED x y w h hue entryId maxChars initialText...
                    var parts = SplitTokens(args, 7, keepRemainder: true);
                    if (parts.Length >= 8)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int maxLen = ParseIntToken(parts[6]);
                        gump.AddTextEntryLimited(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            ResolveDialogHtml(parts[7], _character),
                            maxLen);
                    }
                    break;
                }
                case "TEXTENTRYLIMITED":
                {
                    if (!currentPageVisible) break;
                    // TEXTENTRYLIMITED x y w h hue entryId textIndex maxChars
                    var parts = SplitTokens(args, 8);
                    if (parts.Length >= 8)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[6]);
                        int maxLen = ParseIntToken(parts[7]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddTextEntryLimited(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            txt,
                            maxLen);
                    }
                    break;
                }
                case "TILEPIC":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 3);
                    if (parts.Length >= 3)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddTilePic(x, y, ParseIntToken(parts[2]));
                    }
                    break;
                }
                case "TILEPICHUE":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddTilePicHue(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]));
                    }
                    break;
                }
                case "GROUP":
                {
                    int g = ParseIntToken(args);
                    gump.AddGroup(g);
                    break;
                }
                case "XMFHTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddXmfHtmlGump(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            (uint)ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]) != 0,
                            ParseIntToken(parts[6]) != 0);
                    }
                    break;
                }
                case "XMFHTMLGUMPCOLOR":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 8);
                    if (parts.Length >= 8)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddXmfHtmlGumpColor(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            (uint)ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]) != 0,
                            ParseIntToken(parts[6]) != 0,
                            ParseIntToken(parts[7]));
                    }
                    break;
                }
            }
        }

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (_character == null)
                return;
            var prevSubject = _dialogSubjectUid;
            _dialogSubjectUid = subjectUid;
            try
            {
            // Try the script's [Dialog d_xxx Button] handler first. If a matching
            // ON=buttonId block exists, its body runs and we're done. Otherwise
            // fall back to page navigation behaviour so the old in-dialog page
            // buttons still work. Button 0 = close/escape — still needs to run
            // the ON=0 handler (e.g. ClearCTags).
            if (TryRunScriptDialogButton(dialogId, (int)buttonId, switches, textEntries))
                return;

            if (buttonId == 0)
                return;

            if (buttonId is >= 1 and <= 5000)
            {
                ObjBase? subject = subjectUid.IsValid ? _world.FindObject(subjectUid) : null;
                TryShowScriptDialog(dialogId, (int)buttonId, subject);
            }
            }
            finally
            {
                _dialogSubjectUid = prevSubject;
            }
        });

        return true;
    }

    /// <summary>Execute the script's <c>[Dialog d_xxx Button]</c> <c>ON=buttonId</c>
    /// handler. Wires the dialog response (buttonId, switches, text entries)
    /// into the expression parser so <c>&lt;ArgN&gt;</c>, <c>&lt;Argtxt[N]&gt;</c>
    /// and <c>&lt;Argchk[N]&gt;</c> resolve correctly during evaluation.</summary>
    private bool TryRunScriptDialogButton(string dialogId, int buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null) return false;
        if (_triggerDispatcher?.Runner == null || _commands?.Resources == null) return false;

        if (!TryFindDialogButtonSection(dialogId, out var buttonSection))
            return false;

        // Build a lookup for Argtxt[N] / Argchk[N].
        var textById = new Dictionary<ushort, string>();
        foreach (var te in textEntries)
            textById[te.Id] = te.Text;
        var switchSet = new HashSet<uint>(switches);

        string? Resolve(string varExpr)
        {
            string upper = varExpr.ToUpperInvariant();
            if (upper == "ARGN") return buttonId.ToString();
            if (upper == "ARGV") return buttonId.ToString();
            // ARGCHK (no brackets) — 1 if any switch is flipped, 0 else.
            // ARGCHKID — the ID of the first selected switch (0 if none).
            // Sphere dialogs rely on these two to gate "OK" handlers:
            //   elseif !(<argchk>) src.sysmessage You did not select …
            //   local.n=<eval <argchkid>-1>
            if (upper == "ARGCHK") return switchSet.Count > 0 ? "1" : "0";
            if (upper == "ARGCHKID")
            {
                uint first = 0;
                foreach (var s in switchSet) { if (first == 0 || s < first) first = s; }
                return first.ToString();
            }

            if (TryParseIndexedAccessor(upper, "ARGTXT", out int txtIdx))
                return textById.TryGetValue((ushort)txtIdx, out var txt) ? txt : "";
            if (TryParseIndexedAccessor(upper, "ARGCHK", out int chkIdx))
                return switchSet.Contains((uint)chkIdx) ? "1" : "0";
            // ARGV[N] falls through to the interpreter's default handling
            // so the updated args.ArgString (from a script-side "args=…"
            // assignment) is tokenised, not the button id.
            return null;
        }

        var parser = _triggerDispatcher.Runner.Interpreter.Expressions;
        if (parser == null) return false;

        var prev = parser.DialogArgResolver;
        parser.DialogArgResolver = Resolve;
        try
        {
            // Source-X parity: dialog button target = dialog subject
            // (the inspected object), NOT the GM character. SRC = GM,
            // target = inspected object. This ensures TRYP/INPDLG/property
            // edits operate on the correct object.
            IScriptObj buttonTarget = _character;
            if (_dialogSubjectUid.IsValid)
            {
                var subj = _world.FindObject(_dialogSubjectUid);
                if (subj != null)
                    buttonTarget = subj;
            }

            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(_character)
            {
                Number1 = buttonId,
                ArgString = buttonId.ToString(),
            };

            var posBefore = _character.Position;
            ushort bodyBefore = _character.BodyId;
            ushort hueBefore = _character.Hue.Value;
            var flagsBefore = _character.StatFlags;

            bool ran = _triggerDispatcher.Runner.TryRunDialogButton(
                buttonSection, buttonId, buttonTarget, this, trigArgs);
            if (ran && _character != null)
            {
                bool moved = !_character.Position.Equals(posBefore);
                bool appearance =
                    _character.BodyId != bodyBefore ||
                    _character.Hue.Value != hueBefore ||
                    _character.StatFlags != flagsBefore;

                if (moved)
                {
                    _world.MoveCharacter(_character, _character.Position);
                    Resync();
                    _mountEngine?.EnsureMountedState(_character);
                    BroadcastDrawObject(_character);
                }
                else if (appearance)
                {
                    // No teleport, but body / hue / flag changed (e.g.
                    // statf_hidden via |=). Re-send DrawObject so the
                    // client reflects the new appearance without a
                    // full resync.
                    BroadcastDrawObject(_character);
                }
            }
            return ran;
        }
        finally
        {
            parser.DialogArgResolver = prev;
        }
    }

    private static bool TryParseIndexedAccessor(string upperVar, string prefix, out int index)
    {
        index = 0;
        if (!upperVar.StartsWith(prefix + "[", StringComparison.Ordinal)) return false;
        int close = upperVar.IndexOf(']');
        if (close <= prefix.Length + 1) return false;
        string num = upperVar.Substring(prefix.Length + 1, close - prefix.Length - 1);
        return int.TryParse(num, out index);
    }

    /// <summary>Pre-expand FOR / WHILE / IF / LOCAL blocks in a dialog's
    /// key sequence. Dialog scripts mix render verbs (BUTTON, DTEXT, …)
    /// with control-flow verbs Sphere's interpreter otherwise handles at
    /// runtime. The outer parser walks the section linearly, so we flatten
    /// loops into their rendered copies and evaluate IFs up front.
    /// <paramref name="locals"/> is shared with the caller so LOCAL.x=
    /// assignments stay visible to later expression resolution.</summary>
    private List<SphereNet.Scripting.Parsing.ScriptKey> ExpandDialogScriptKeys(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input,
        Dictionary<string, string> locals,
        int dialogArgN1)
    {
        var output = new List<SphereNet.Scripting.Parsing.ScriptKey>(input.Count);
        ExpandRange(input, 0, input.Count, output, locals, dialogArgN1);
        return output;
    }

    private void ExpandRange(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end,
        List<SphereNet.Scripting.Parsing.ScriptKey> output,
        Dictionary<string, string> locals,
        int dialogArgN1)
    {
        int i = start;
        while (i < end)
        {
            var k = input[i];
            string cmd = k.Key.Trim().ToUpperInvariant();
            string args = k.Arg;

            if (cmd == "IF")
            {
                int ifEnd = FindBlockEnd(input, i + 1, end, "IF", "ENDIF");
                if (ifEnd < 0) { i = end; break; }
                // Split the IF body into IF / ELIF / ELSE branches.
                var branches = SplitIfBranches(input, i + 1, ifEnd);
                string resolvedCond = ResolveInlineExpressions(args, locals, dialogArgN1);
                bool taken = EvaluateDialogCondition(resolvedCond);
                int chosenStart = -1, chosenEnd = -1;
                if (taken) { chosenStart = branches[0].Start; chosenEnd = branches[0].End; }
                else
                {
                    for (int b = 1; b < branches.Count && chosenStart < 0; b++)
                    {
                        var br = branches[b];
                        if (br.Keyword == "ELSE")
                        { chosenStart = br.Start; chosenEnd = br.End; break; }
                        if (br.Keyword == "ELIF" || br.Keyword == "ELSEIF")
                        {
                            string elifCond = ResolveInlineExpressions(br.Condition!, locals, dialogArgN1);
                            if (EvaluateDialogCondition(elifCond))
                            { chosenStart = br.Start; chosenEnd = br.End; break; }
                        }
                    }
                }
                if (chosenStart >= 0)
                    ExpandRange(input, chosenStart, chosenEnd, output, locals, dialogArgN1);
                i = ifEnd + 1;
                continue;
            }

            if (cmd == "FOR")
            {
                // FOR N  / FOR START END / FOR VAR START END.
                int forEnd = FindBlockEnd(input, i + 1, end, "FOR", "ENDFOR");
                if (forEnd < 0) { i = end; break; }
                string resolved = ResolveInlineExpressions(args, locals, dialogArgN1);
                ParseForRange(resolved, out string? iterName, out long from, out long to);
                const long maxIter = 500;
                long count = Math.Min(maxIter, to - from + 1);
                string? savedFor = locals.TryGetValue("_FOR", out var sf) ? sf : null;
                string? savedNamed = (iterName != null && locals.TryGetValue(iterName, out var sv)) ? sv : null;
                for (long it = 0; it < count; it++)
                {
                    string cur = (from + it).ToString();
                    locals["_FOR"] = cur;
                    if (iterName != null)
                        locals[iterName] = cur;
                    ExpandRange(input, i + 1, forEnd, output, locals, dialogArgN1);
                }
                if (savedFor != null) locals["_FOR"] = savedFor; else locals.Remove("_FOR");
                if (iterName != null)
                {
                    if (savedNamed != null) locals[iterName] = savedNamed;
                    else locals.Remove(iterName);
                }
                i = forEnd + 1;
                continue;
            }

            // Dialog scripts frequently seed runtime state in the layout body
            // before drawing controls:
            //   ARGS <def.npctype_<dctag0.spawn_type>_spawn>
            //   SRC.CTAG0.spawn_type 1
            // Keep those side-effect lines in the pre-expansion pass so
            // later <ARGV[...]> / <DARGV> and CTAG-dependent expressions
            // resolve correctly.
            if (cmd == "ARGS")
            {
                string resolvedArgs = string.IsNullOrEmpty(args)
                    ? ""
                    : ResolveInlineExpressions(args, locals, dialogArgN1);
                locals["__ARGS"] = resolvedArgs;
                i++;
                continue;
            }
            if ((cmd.StartsWith("SRC.CTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.CTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.DCTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase)) &&
                _character != null)
            {
                string prop = cmd.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase) ? cmd[4..] : cmd;
                string resolvedVal = string.IsNullOrEmpty(args)
                    ? ""
                    : ResolveInlineExpressions(args, locals, dialogArgN1);
                _character.TrySetProperty(prop, resolvedVal);
                i++;
                continue;
            }

            if (cmd == "WHILE")
            {
                int whileEnd = FindBlockEnd(input, i + 1, end, "WHILE", "ENDWHILE");
                if (whileEnd < 0) { i = end; break; }
                const int maxIter = 500;
                int iter = 0;
                while (iter < maxIter)
                {
                    string resolved = ResolveInlineExpressions(args, locals, dialogArgN1);
                    if (!EvaluateDialogCondition(resolved)) break;
                    ExpandRange(input, i + 1, whileEnd, output, locals, dialogArgN1);
                    iter++;
                }
                i = whileEnd + 1;
                continue;
            }

            // REFn = <uid> — scope-local object reference. Storage
            // lives in the same `locals` dict under the key "REFn" so
            // subsequent <REFn> / <REFn.property> lookups see it.
            // Scripts like the admin panel rely on this to point rows
            // at account / character objects:
            //   REF1=<SERV.ACCOUNT.<Eval <CTag0.Dialog.Admin.Index>+1>>
            //   <DEF.admin_flag_1>: <REF1.NAME>
            if (cmd.Length > 3 && cmd.StartsWith("REF", StringComparison.OrdinalIgnoreCase) &&
                char.IsDigit(cmd[3]) && !cmd.Contains('.'))
            {
                string resolved = string.IsNullOrEmpty(args) ? "" : ResolveInlineExpressions(args, locals, dialogArgN1);
                locals[cmd.ToUpperInvariant()] = resolved;
                i++;
                continue;
            }

            if (cmd.StartsWith("LOCAL.", StringComparison.OrdinalIgnoreCase))
            {
                // LOCAL.x = value  or  LOCAL.x += N
                string nameAndOp = cmd[6..]; // after "LOCAL."
                // Detect compound operator in args: "+= 1", "= 5", "1", etc.
                string varName = nameAndOp;
                string valueExpr = args;
                char opCh = ' ';
                if (valueExpr.Length > 0)
                {
                    var trimmed = valueExpr.TrimStart();
                    if (trimmed.StartsWith("+=", StringComparison.Ordinal)) { opCh = '+'; valueExpr = trimmed[2..]; }
                    else if (trimmed.StartsWith("-=", StringComparison.Ordinal)) { opCh = '-'; valueExpr = trimmed[2..]; }
                    else if (trimmed.StartsWith("=", StringComparison.Ordinal)) { opCh = '='; valueExpr = trimmed[1..]; }
                }
                string resolved = ResolveInlineExpressions(valueExpr.Trim(), locals, dialogArgN1);
                long num = ParseLongToken(resolved);
                long current = locals.TryGetValue(varName, out var cur) && long.TryParse(cur, out long pv) ? pv : 0;
                long next = opCh switch
                {
                    '+' => current + num,
                    '-' => current - num,
                    _ => num,
                };
                locals[varName] = next.ToString();
                i++;
                continue;
            }

            // Render command — inline-resolve the args and emit. Local
            // scope flows into the args via ResolveInlineExpressions so
            // <local.x> / <eval …> inside BUTTON / DTEXT / RADIO /
            // RESIZEPIC coordinates come out as concrete numbers.
            string resolvedArg = string.IsNullOrEmpty(args)
                ? args
                : ResolveInlineExpressions(args, locals, dialogArgN1);
            output.Add(new SphereNet.Scripting.Parsing.ScriptKey(k.Key, resolvedArg));
            i++;
        }
    }

    /// <summary>Find the matching end keyword, honouring nested blocks.
    /// Returns the absolute index of the end keyword, or -1 if unmatched.</summary>
    private static int FindBlockEnd(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end,
        string openKeyword, string endKeyword)
    {
        int depth = 1;
        for (int i = start; i < end; i++)
        {
            string k = input[i].Key.Trim().ToUpperInvariant();
            if (k == openKeyword) depth++;
            else if (k == endKeyword) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private readonly record struct IfBranch(string Keyword, int Start, int End, string? Condition);

    private static List<IfBranch> SplitIfBranches(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end)
    {
        // Branch boundaries at ELIF / ELSEIF / ELSE at depth 0 (relative
        // to the outer IF we're inside). Nested IFs count as depth.
        var list = new List<IfBranch>();
        int depth = 0;
        int segStart = start;
        string curKeyword = "IF";
        string? curCondition = null;
        for (int i = start; i < end; i++)
        {
            string k = input[i].Key.Trim().ToUpperInvariant();
            if (k == "IF") { depth++; continue; }
            if (k == "ENDIF") { depth--; continue; }
            if (depth != 0) continue;
            if (k == "ELSE" || k == "ELIF" || k == "ELSEIF")
            {
                list.Add(new IfBranch(curKeyword, segStart, i, curCondition));
                curKeyword = k;
                curCondition = (k != "ELSE") ? input[i].Arg : null;
                segStart = i + 1;
            }
        }
        list.Add(new IfBranch(curKeyword, segStart, end, curCondition));
        return list;
    }

    private static void ParseForRange(string expr, out string? iterName, out long from, out long to)
    {
        iterName = null;
        from = 0; to = 0;
        var parts = expr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            // FOR N — loops 0 to N-1 in Sphere.
            long.TryParse(parts[0], out long n);
            to = n - 1;
        }
        else if (parts.Length >= 2)
        {
            // FOR var start end
            if (!long.TryParse(parts[0], out from))
            {
                iterName = parts[0];
                if (parts.Length >= 3)
                {
                    long.TryParse(parts[1], out from);
                    long.TryParse(parts[2], out to);
                }
                return;
            }
            long.TryParse(parts[1], out to);
        }
    }

    private static long ParseLongToken(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out long hv))
            return hv;
        return long.TryParse(s, out long v) ? v : 0;
    }

    /// <summary>Cheap-and-cheerful truthiness for IF / WHILE. An expression
    /// is truthy if it evaluates to non-zero; strings compare as text.
    /// The Sphere parser accepts relational operators, which
    /// ExpressionParser already understands — we just evaluate through it.</summary>
    private bool EvaluateDialogCondition(string condition)
    {
        string c = condition.Trim();
        if (string.IsNullOrEmpty(c)) return false;
        if (c.Length >= 2 && c[0] == '(' && c[^1] == ')')
            c = c[1..^1].Trim();
        if (string.IsNullOrEmpty(c) || c == "0")
            return false;

        bool hasOperator = c.AsSpan().IndexOfAny("!+-*/%&|()<>=~^") >= 0;

        var parser = new ExpressionParser();
        long v = parser.Evaluate(c.AsSpan());
        if (v != 0)
            return true;
        if (hasOperator)
            return false;
        bool truthy = !long.TryParse(c, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        return truthy;
    }

    /// <summary>Resolve &lt;local.x&gt; / &lt;dlocal.x&gt; / &lt;eval …&gt;
    /// / &lt;def0.…&gt; / &lt;src.…&gt; etc. in an argument string, using
    /// the dialog-local scope for LOCAL references. <paramref name="dialogArgN1"/>
    /// feeds &lt;argn1&gt; / &lt;argn&gt; so the Sphere "page &lt;argn1&gt;"
    /// pattern in dialog layouts resolves to the page the dialog was
    /// opened on (e.g. sdialog d_moongates &lt;eval &lt;src.p.m&gt;+1&gt;).</summary>
    private string ResolveInlineExpressions(string input, Dictionary<string, string> locals, int dialogArgN1 = 0)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('<') < 0) return input;

        var servResolver = _triggerDispatcher?.Runner?.Interpreter?.ServerPropertyResolver;
        var parser = new ExpressionParser
        {
            VariableResolver = varName =>
            {
                string upper = varName.ToUpperInvariant();
                if (upper == "ARGN" || upper == "ARGN1")
                    return dialogArgN1.ToString();
                if (upper == "ARGS" || upper == "DARGS")
                    return locals.TryGetValue("__ARGS", out var av) ? av : "";
                if (upper == "DARGV")
                {
                    string rawArgs = locals.TryGetValue("__ARGS", out var av) ? av : "";
                    var toks = rawArgs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return toks.Length.ToString();
                }
                if (upper.StartsWith("ARGV", StringComparison.Ordinal))
                {
                    string rawArgs = locals.TryGetValue("__ARGS", out var av) ? av : "";
                    var toks = rawArgs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string suffix = upper.Length > 4 ? upper[4..] : "";
                    if (suffix.StartsWith("[", StringComparison.Ordinal) && suffix.EndsWith("]", StringComparison.Ordinal) && suffix.Length > 2)
                        suffix = suffix[1..^1];
                    if (int.TryParse(suffix, out int idx) && idx >= 0 && idx < toks.Length)
                        return toks[idx];
                    return "";
                }
                // Uninitialised LOCAL / DLOCAL return "0" per Sphere
                // convention — scripts read them as zero before the first
                // assignment (common pattern: "while <dlocal.n>" where the
                // counter is bumped inside the body).
                if (upper.StartsWith("LOCAL."))
                    return locals.TryGetValue(upper[6..], out var lv) ? lv : "0";
                if (upper.StartsWith("DLOCAL."))
                    return locals.TryGetValue(upper[7..], out var dlv) ? dlv : "0";

                // REFn and REFn.property — dialog-scoped object references.
                // REF1..REF999 are stored in the same locals dict keyed as
                // "REFN" (upper). `<REFn>` returns the stored reference
                // string (usually a UID); `<REFn.property>` looks up the
                // referenced object via the REF_GET protocol so its
                // properties flow into the rendered layout.
                if (upper.Length > 3 && upper.StartsWith("REF", StringComparison.Ordinal) &&
                    char.IsDigit(upper[3]))
                {
                    int dotIdx = upper.IndexOf('.');
                    string refKey = dotIdx > 0 ? upper[..dotIdx] : upper;
                    string? refVal = locals.TryGetValue(refKey, out var rv) ? rv : null;
                    if (dotIdx < 0) return refVal ?? "0";
                    if (string.IsNullOrEmpty(refVal) || refVal == "0") return "0";
                    string subProp = upper[(dotIdx + 1)..];
                    return servResolver?.Invoke($"_REF_GET={refVal}|{subProp}") ?? "0";
                }

                // CTAG0.X / CTAG.X / DCTAG0.X / DCTAG.X on the current character. Reads from
                // the client-session CTag map (Source-X CClient::m_TagDefs
                // parity), not the persistent TAG storage. Defaults to
                // "0" when unset — Sphere convention.
                if (upper.StartsWith("CTAG0.") || upper.StartsWith("CTAG.") ||
                    upper.StartsWith("DCTAG0.") || upper.StartsWith("DCTAG."))
                {
                    int dot = upper.IndexOf('.');
                    string tagKey = upper[(dot + 1)..];
                    string? tagVal = _character?.CTags.Get(tagKey);
                    if (string.IsNullOrEmpty(tagVal) && tagKey.Equals("ACCOUNTLANG", StringComparison.OrdinalIgnoreCase))
                    {
                        string fallbackLang = GetEffectiveAccountLang();
                        if (!string.IsNullOrEmpty(fallbackLang))
                            return fallbackLang;
                    }
                    return tagVal ?? "0";
                }
                if ((upper.StartsWith("DEF.") || upper.StartsWith("DEF0.")) &&
                    _commands?.Resources != null)
                {
                    string origKey = varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase)
                        ? varName[4..]
                        : varName[5..];
                    if (_commands.Resources.TryGetDefValue(origKey, out string defTextVal))
                    {
                        string stripped = StripSurroundingQuotes(defTextVal);
                        return stripped;
                    }
                    var defRid = _commands.Resources.ResolveDefName(origKey);
                    if (defRid.IsValid) return defRid.Index.ToString();
                    return "0";
                }
                if ((upper.StartsWith("SRC.") || upper.StartsWith("DSRC.")) && _character != null)
                {
                    int d = upper.IndexOf('.');
                    string sub = upper[(d + 1)..];
                    if (_character.TryGetProperty(sub, out string srcVal))
                    {
                        if ((sub.Equals("CTAG0.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase) ||
                             sub.Equals("CTAG.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(srcVal) || srcVal == "0"))
                            return GetEffectiveAccountLang();
                        return srcVal;
                    }
                }
                if (upper.StartsWith("SERV.") && servResolver != null)
                    return servResolver(upper[5..]);

                // Dialog subject (CLIMODE_DIALOG pObj) wins over GM
                // properties for bare reads — <BODY>, <STR>, <NAME>
                // inside d_charprop1 refer to the inspected target,
                // not the GM. Fall back to GM when subject misses so
                // admin-style dialogs keep their existing behaviour.
                if (_dialogSubjectUid.IsValid)
                {
                    var subj = _world.FindObject(_dialogSubjectUid);
                    if (subj != null)
                    {
                        // Sphere <I.*> alias = the subject itself.
                        //   <I.STR> → subject STR, <I.0> → skill 0 level.
                        // Character.TryGetProperty doesn't know the "I."
                        // prefix, so strip it here and delegate the rest.
                        string lookup = upper.StartsWith("I.", StringComparison.Ordinal)
                            ? upper[2..]
                            : upper;
                        // A bare number on Character resolves to that skill's
                        // current level — matches Source-X CChar::r_WriteVal
                        // on an integer key.
                        if (subj is Character subjCh && int.TryParse(lookup, out int skillIdx)
                            && skillIdx >= 0 && skillIdx < (int)SkillType.Qty)
                            return subjCh.GetSkill((SkillType)skillIdx).ToString();
                        if (subj.TryGetProperty(lookup, out string subjProp))
                            return subjProp;
                    }
                }
                if (_character != null && _character.TryGetProperty(upper, out string charProp))
                    return charProp;

                // Last-resort delegation: the same resolver the script
                // interpreter uses at runtime covers ACCOUNT.x,
                // ISEVENT.x, ISDIALOGOPEN.x, VAR0.x, GETREFTYPE, and
                // other dialog-common accessors.
                if (_character != null &&
                    TryResolveScriptVariable(upper, _character, null, out string fallback))
                    return fallback;

                // Bare defname constants used in script arithmetic/bit tests,
                // e.g. <statf_insubstantial>, <memory_ipet>.
                if (_commands?.Resources != null && IsPlainDefToken(upper))
                {
                    var rid = _commands.Resources.ResolveDefName(upper);
                    if (rid.IsValid) return rid.Index.ToString();
                }

                return null;
            },
        };
        return parser.EvaluateStr(input);
    }

    private bool TryFindDialogButtonSection(string dialogId, out SphereNet.Scripting.Parsing.ScriptSection buttonSection)
    {
        buttonSection = null!;
        if (_commands?.Resources == null) return false;

        foreach (var script in _commands.Resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                var sections = file.ReadAllSections();
                foreach (var section in sections)
                {
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Header forms: "d_xxx BUTTON" / "d_xxx TEXT" / etc.
                    // Split on whitespace and match (first=id, second=BUTTON).
                    var parts = section.Argument.Split(
                        new[] { ' ', '\t' },
                        2,
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!parts[0].Equals(dialogId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!parts[1].Equals("BUTTON", StringComparison.OrdinalIgnoreCase)) continue;

                    buttonSection = section;
                    return true;
                }
            }
            finally
            {
                script.Close();
            }
        }
        return false;
    }

    private bool TryFindDialogSections(string dialogId, out SphereNet.Scripting.Parsing.ScriptSection layoutSection)
    {
        layoutSection = null!;
        if (_commands?.Resources == null)
            return false;

        foreach (var script in _commands.Resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                var sections = file.ReadAllSections();
                foreach (var section in sections)
                {
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string arg = section.Argument.Trim();
                    if (arg.Equals(dialogId, StringComparison.OrdinalIgnoreCase))
                    {
                        layoutSection = section;
                        return true;
                    }
                }
            }
            finally
            {
                script.Close();
            }
        }

        return false;
    }



    private bool TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection)
    {
        menuSection = null!;
        if (_commands?.Resources == null)
            return false;

        foreach (var script in _commands.Resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                var sections = file.ReadAllSections();
                foreach (var section in sections)
                {
                    if (!section.Name.Equals("MENU", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string arg = section.Argument.Trim();
                    if (arg.Equals(menuDefname, StringComparison.OrdinalIgnoreCase))
                    {
                        menuSection = section;
                        return true;
                    }
                }
            }
            finally
            {
                script.Close();
            }
        }

        return false;
    }

    private string ResolveDialogHtml(string html, IScriptObj target)
    {
        // Delegate through the same resolver chain the interpreter uses.
        // SERV.*, RTIME, RTICKS, REFn.property, etc. all live on the server
        // property resolver — without routing dialog text through it we lost
        // the most common Sphere gump substitutions (<Serv.Servname>, …).
        var servResolver = _triggerDispatcher?.Runner?.Interpreter?.ServerPropertyResolver;
        var parser = new ExpressionParser
        {
            VariableResolver = varName =>
            {
                if (varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) &&
                    _commands?.Resources != null &&
                    _commands.Resources.TryGetDefValue(varName[4..], out string defVal))
                {
                    return defVal;
                }

                if (TryResolveScriptVariable(varName, target, null, out string runtimeVal))
                    return runtimeVal;

                // Source/target routing: Src.X resolves through the admin's
                // own character. Admin dialogs reference <Src.Version>,
                // <Src.Account>, <Src.CTag0.…>, etc.
                if (varName.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase))
                {
                    string subProp = varName[4..];
                    if (_character != null && _character.TryGetProperty(subProp, out string srcProp))
                    {
                        if ((subProp.Equals("CTAG0.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase) ||
                             subProp.Equals("CTAG.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(srcProp) || srcProp == "0"))
                            return GetEffectiveAccountLang();
                        return srcProp;
                    }
                }

                if (target.TryGetProperty(varName, out string prop))
                    return prop;

                // SERV.* / RTIME / RTICKS — delegate to the runtime resolver.
                if (servResolver != null)
                {
                    if (varName.StartsWith("SERV.", StringComparison.OrdinalIgnoreCase))
                    {
                        string servProp = varName[5..];
                        string? servVal = servResolver(servProp);
                        if (servVal != null) return servVal;
                    }
                    if (varName.StartsWith("RTIME", StringComparison.OrdinalIgnoreCase) ||
                        varName.StartsWith("RTICKS", StringComparison.OrdinalIgnoreCase))
                    {
                        string? rVal = servResolver(varName);
                        if (rVal != null) return rVal;
                    }
                    // Bare server metrics (CLIENTS, ACCOUNTS, CHARS, ITEMS, VERSION,
                    // SERVNAME, TIME, SAVECOUNT, MEM, REGEN0-3) as fallback.
                    string? bare = servResolver(varName);
                    if (bare != null) return bare;
                }

                if (_commands?.Resources != null && IsPlainDefToken(varName))
                {
                    var rid = _commands.Resources.ResolveDefName(varName);
                    if (rid.IsValid) return rid.Index.ToString();
                }

                return null;
            }
        };

        return parser.EvaluateStr(html);
    }

    private static bool IsPlainDefToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        foreach (char ch in token)
        {
            bool ok = char.IsLetterOrDigit(ch) || ch is '_' or '.';
            if (!ok) return false;
        }
        return true;
    }

    private string GetEffectiveAccountLang()
    {
        if (_character != null && _character.TryGetProperty("ACCOUNT.LANG", out string langRaw))
        {
            string lang = (langRaw ?? "").Trim().ToUpperInvariant();
            if (lang.Length == 0) return "ENG";
            return lang switch
            {
                "ENU" => "ENG",
                "FRB" or "FRC" => "FRA",
                "ESN" => "ESP",
                _ => lang
            };
        }
        return "ENG";
    }

    /// <summary>Resolve a dialog coordinate token.
    /// Formats:
    ///   N      — absolute (resets cursor to N)
    ///   +N     — cursor += N
    ///   *N     — rowCursor += N; cursor = rowCursor (next-row step, independent
    ///            of the +/- column walk)
    /// <paramref name="rowCursor"/> may alias <paramref name="cursor"/> when the
    /// caller hasn't wired a separate row tracker (old call sites).</summary>
    private static int ResolveDialogCoord(string token, ref int cursor, ref int rowCursor)
    {
        // Sphere DORIGIN coord rules (verified against d_SphereAdmin_PlayerTweak):
        //   bare N : SET baseline to N and return it (origin offset reset)
        //   +N     : return baseline + N (NON-mutating row-relative offset)
        //   -N     : return baseline - N (NON-mutating row-relative offset)
        //   *N     : baseline += N, return baseline (advance the row)
        //
        // The earlier "+N means cursor += N" reading was wrong: with the
        // DORIGIN block
        //     DText  +35 +0   _Properties
        //     Button +0  -2   4005 4006 0 3 0
        // the button has to land at origin.x (X=5), to the LEFT of the
        // text. Cumulative cursor logic stuck the button at X=40 on top
        // of the label, which was the visible "buttons drift sideways /
        // text is unreadable" symptom in d_SphereAdmin_PlayerTweak.
        // `cursor` is kept around as a back-compat alias mirroring the
        // baseline so callers that still pass it observe the same value
        // as `rowCursor`.
        token = token.Trim();
        if (token.StartsWith('+'))
        {
            int delta = ParseIntToken(token[1..]);
            return rowCursor + delta;
        }
        if (token.StartsWith('-'))
        {
            int delta = ParseIntToken(token[1..]);
            return rowCursor - delta;
        }
        if (token.StartsWith('*'))
        {
            int delta = ParseIntToken(token[1..]);
            rowCursor += delta;
            cursor = rowCursor;
            return rowCursor;
        }

        rowCursor = ParseIntToken(token);
        cursor = rowCursor;
        return rowCursor;
    }

    private static int ResolveDialogCoord(string token, ref int cursor)
    {
        int dummy = cursor;
        return ResolveDialogCoord(token, ref cursor, ref dummy);
    }

    /// <summary>DEFNAME text values in Sphere scripts often ship wrapped
    /// in double quotes (<c>CharFlag.1 "Invulnerable"</c>). The quotes
    /// are a Sphere source-lexer convention, not part of the payload —
    /// strip a single matched pair when resolving so the gump label
    /// reads "Invulnerable" instead of <c>"Invulnerable"</c>.</summary>
    private static string StripSurroundingQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static int ParseIntToken(string token)
    {
        token = token.Trim();
        if (token.Length == 0)
            return 0;

        // Sphere convention (matches ScriptKey.TryParseNumber and Source-X
        // CExpression::GetVal): a leading '0' on a multi-digit token marks
        // the value as HEX. Without this rule "0480" silently parsed as
        // decimal 480 instead of 0x480 (1152) and admin gump hues came
        // out as random off-spectrum colors — the d_SphereAdmin_PlayerTweak
        // labels were rendered in colors the client treats as
        // near-invisible (the "yazılar okunmuyor" symptom).

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
            return hex;

        if (token.Length > 1 && token[0] == '0' &&
            int.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out int legacyHex))
            return legacyHex;

        if (int.TryParse(token, out int dec))
            return dec;

        return 0;
    }

    private static string[] SplitTokens(string input, int minLeadingTokens, bool keepRemainder = false)
    {
        if (!keepRemainder)
            return input.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var parts = new List<string>();
        string text = input.Trim();
        int i = 0;
        while (i < text.Length && parts.Count < minLeadingTokens)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            if (i >= text.Length) break;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ',') i++;
            parts.Add(text[start..i]);
        }

        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
        parts.Add(i < text.Length ? text[i..] : "");
        return parts.ToArray();
    }
}
