using SphereNet.Network.Packets;

namespace SphereNet.Network.Packets.Incoming;

/// <summary>0x06 — Double click (use/open).</summary>
public sealed class PacketDoubleClick : PacketHandler
{
    public PacketDoubleClick() : base(0x06, 5) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        state.OnDoubleClick(serial);
    }
}

/// <summary>0x09 — Single click (look).</summary>
public sealed class PacketSingleClick : PacketHandler
{
    public PacketSingleClick() : base(0x09, 5) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        state.OnSingleClick(serial);
    }
}

/// <summary>0x07 — Pick up item.</summary>
public sealed class PacketItemPickup : PacketHandler
{
    public PacketItemPickup() : base(0x07, 7) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        ushort amount = buffer.ReadUInt16();
        state.OnItemPickup(serial, amount);
    }
}

/// <summary>0x08 — Drop item (pre-SA).</summary>
public sealed class PacketItemDrop : PacketHandler
{
    public PacketItemDrop() : base(0x08, 14) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        short x = buffer.ReadInt16();
        short y = buffer.ReadInt16();
        sbyte z = buffer.ReadSByte();
        if (state.IsClientPost6017)
            buffer.ReadByte(); // grid index (6.0.1.7+)
        uint container = buffer.ReadUInt32();
        state.OnItemDrop(serial, x, y, z, container);
    }
}

/// <summary>0x13 — Equip item request.</summary>
public sealed class PacketItemEquip : PacketHandler
{
    public PacketItemEquip() : base(0x13, 10) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        byte layer = buffer.ReadByte();
        uint container = buffer.ReadUInt32();
        state.OnItemEquip(serial, layer, container);
    }
}

/// <summary>0x34 — Status request.</summary>
public sealed class PacketStatusRequest : PacketHandler
{
    public PacketStatusRequest() : base(0x34, 10) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint edCode = buffer.ReadUInt32();
        byte type = buffer.ReadByte();
        uint serial = buffer.ReadUInt32();
        state.OnStatusRequest(type, serial);
    }
}

/// <summary>0xB8 — Profile request (variable length).</summary>
public sealed class PacketProfileRequest : PacketHandler
{
    public PacketProfileRequest() : base(0xB8, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte mode = buffer.ReadByte(); // 0=request, 1=set
        uint serial = buffer.ReadUInt32();
        string bioText = "";
        if (mode == 1)
        {
            ushort cmdType = buffer.ReadUInt16();
            ushort textLen = buffer.ReadUInt16();
            if (textLen > 0)
                bioText = buffer.ReadUnicodeFixed(textLen);
        }
        state.OnProfileRequest(mode, serial, bioText);
    }
}

/// <summary>0x6C — Target response.</summary>
public sealed class PacketTargetResponse : PacketHandler
{
    public PacketTargetResponse() : base(0x6C, 19) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte type = buffer.ReadByte();
        uint targetId = buffer.ReadUInt32();
        byte flags = buffer.ReadByte();
        uint serial = buffer.ReadUInt32();
        short x = buffer.ReadInt16();
        short y = buffer.ReadInt16();
        buffer.ReadByte(); // unknown
        sbyte z = buffer.ReadSByte();
        ushort graphic = buffer.ReadUInt16();
        state.OnTargetResponse(type, targetId, serial, x, y, z, graphic);
    }
}

/// <summary>0xAD — Unicode speech request.</summary>
public sealed class PacketSpeechUnicode : PacketHandler
{
    public PacketSpeechUnicode() : base(0xAD, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte type = buffer.ReadByte();
        ushort hue = buffer.ReadUInt16();
        ushort font = buffer.ReadUInt16();
        string lang = buffer.ReadAsciiFixed(4);

        string text;
        bool isEncoded = (type & 0xC0) != 0;
        if (isEncoded)
        {
            // Keyword/encoded speech. Layout (ServUO PacketHandlers.cs
            // UnicodeSpeech): two bytes pack a 12-bit keyword count (high)
            // plus a 4-bit carry (low); keyword IDs alternate between
            // using the 4-bit carry + next byte and reading a new pair.
            // After the keyword block the text is UTF-8 null-terminated —
            // NOT big-endian unicode as in plain mode.
            int value = buffer.ReadUInt16();
            int count = (value & 0xFFF0) >> 4;
            int hold = value & 0xF;

            if (count < 0 || count > 50)
            {
                // Malformed — bail with empty text rather than over-reading.
                state.OnSpeech((byte)(type & 0x3F), hue, font, "");
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if ((i & 1) == 0)
                {
                    hold <<= 8;
                    hold |= buffer.ReadByte();
                    // keyword id = hold; we discard — server routes on text
                    hold = 0;
                }
                else
                {
                    value = buffer.ReadUInt16();
                    // keyword id = (value & 0xFFF0) >> 4
                    hold = value & 0xF;
                }
            }

            // UTF-8 bytes until null — cap to prevent OOM from malicious packets.
            const int MaxSpeechBytes = 4096;
            var utf8 = new List<byte>(64);
            while (buffer.Remaining > 0 && utf8.Count < MaxSpeechBytes)
            {
                byte b = buffer.ReadByte();
                if (b == 0) break;
                utf8.Add(b);
            }
            text = System.Text.Encoding.UTF8.GetString(utf8.ToArray());

            // Strip the encoded flag so downstream speech routing sees a
            // plain speech type (0..0x3F).
            type &= 0x3F;
        }
        else
        {
            text = buffer.ReadUnicodeNullBE();
        }

        state.OnSpeech(type, hue, font, text);
    }
}

/// <summary>0xB1 — Gump dialog response.</summary>
public sealed class PacketGumpResponse : PacketHandler
{
    public PacketGumpResponse() : base(0xB1, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        uint gumpId = buffer.ReadUInt32();
        uint buttonId = buffer.ReadUInt32();

        const int MaxSwitches = 1024;
        const int MaxTextEntries = 256;
        uint switchCount = buffer.Remaining >= 4 ? Math.Min(buffer.ReadUInt32(), MaxSwitches) : 0;
        var switches = new uint[switchCount];
        for (int i = 0; i < switchCount && buffer.Remaining >= 4; i++)
            switches[i] = buffer.ReadUInt32();

        uint textCount = buffer.Remaining >= 4 ? Math.Min(buffer.ReadUInt32(), MaxTextEntries) : 0;
        var textEntries = new (ushort Id, string Text)[textCount];
        for (int i = 0; i < textCount && buffer.Remaining >= 4; i++)
        {
            ushort id = buffer.ReadUInt16();
            ushort len = Math.Min(buffer.ReadUInt16(), (ushort)1024);
            string text = buffer.ReadUnicodeFixed(len);
            textEntries[i] = (id, text);
        }

        state.OnGumpResponse(serial, gumpId, buttonId, switches, textEntries);
    }
}

/// <summary>0xBD — Client version string.</summary>
public sealed class PacketClientVersion : PacketHandler
{
    public PacketClientVersion() : base(0xBD, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        string version = buffer.ReadAsciiNull();
        state.OnClientVersion(version);
    }
}

/// <summary>0xBF — Extended command (sub-opcode router).</summary>
public sealed class PacketExtendedCommand : PacketHandler
{
    public PacketExtendedCommand() : base(0xBF, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        ushort subCmd = buffer.ReadUInt16();
        state.OnExtendedCommand(subCmd, buffer);
    }
}

/// <summary>0xD7 — Encoded command (sub-opcode router).</summary>
public sealed class PacketEncodedCommand : PacketHandler
{
    public PacketEncodedCommand() : base(0xD7, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        ushort subCmd = buffer.ReadUInt16();
        state.OnExtendedCommand(subCmd, buffer);
    }
}

/// <summary>0xD6 — AOS Tooltip request.</summary>
public sealed class PacketAOSTooltipReq : PacketHandler
{
    public PacketAOSTooltipReq() : base(0xD6, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        while (buffer.Remaining >= 4)
        {
            uint serial = buffer.ReadUInt32();
            state.OnAOSTooltip(serial);
        }
    }
}

/// <summary>Buy item entry from client vendor buy packet.</summary>
public readonly struct VendorBuyEntry
{
    public byte Layer { get; init; }
    public uint ItemSerial { get; init; }
    public ushort Amount { get; init; }
}

/// <summary>Sell item entry from client vendor sell packet.</summary>
public readonly struct VendorSellEntry
{
    public uint ItemSerial { get; init; }
    public ushort Amount { get; init; }
}

/// <summary>0x3B — Vendor buy request.</summary>
public sealed class PacketVendorBuy : PacketHandler
{
    public PacketVendorBuy() : base(0x3B, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint vendorSerial = buffer.ReadUInt32();
        byte flag = buffer.ReadByte();

        const int MaxVendorItems = 255;
        var items = new List<VendorBuyEntry>();
        if (flag != 0)
        {
            while (buffer.Remaining >= 7 && items.Count < MaxVendorItems)
            {
                byte layer = buffer.ReadByte();
                uint serial = buffer.ReadUInt32();
                ushort amount = buffer.ReadUInt16();
                items.Add(new VendorBuyEntry { Layer = layer, ItemSerial = serial, Amount = amount });
            }
        }
        state.OnVendorBuy(vendorSerial, flag, items);
    }
}

/// <summary>0x9F — Vendor sell request.</summary>
public sealed class PacketVendorSell : PacketHandler
{
    public PacketVendorSell() : base(0x9F, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint vendorSerial = buffer.ReadUInt32();
        ushort count = buffer.ReadUInt16();

        var items = new List<VendorSellEntry>();
        int maxSell = Math.Min((int)count, 255);
        for (int i = 0; i < maxSell && buffer.Remaining >= 6; i++)
        {
            uint serial = buffer.ReadUInt32();
            ushort amount = buffer.ReadUInt16();
            items.Add(new VendorSellEntry { ItemSerial = serial, Amount = amount });
        }
        state.OnVendorSell(vendorSerial, items);
    }
}

/// <summary>0x6F — Secure trade response.</summary>
public sealed class PacketSecureTrade : PacketHandler
{
    public PacketSecureTrade() : base(0x6F, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte action = buffer.ReadByte();
        uint sessionId = buffer.ReadUInt32();
        uint param = buffer.Length > 9 ? buffer.ReadUInt32() : 0;
        state.OnSecureTrade(action, sessionId, param);
    }
}

/// <summary>0x75 — Rename request.</summary>
public sealed class PacketRename : PacketHandler
{
    public PacketRename() : base(0x75, 35) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        string name = buffer.ReadAsciiFixed(30);
        state.OnRename(serial, name);
    }
}

/// <summary>0xC8 — View range request (EC client).</summary>
public sealed class PacketViewRange : PacketHandler
{
    public PacketViewRange() : base(0xC8, 2) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte range = buffer.ReadByte();
        state.OnViewRange(range);
    }
}

// ==================== Phase 1: Critical Stability ====================

/// <summary>0x2C — Death menu response (resurrect/ghost choice).</summary>
public sealed class PacketDeathMenu : PacketHandler
{
    public PacketDeathMenu() : base(0x2C, 2) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte action = buffer.ReadByte(); // 0=request menu, 1=resurrect, 2=ghost
        state.OnDeathMenu(action);
    }
}

/// <summary>0x83 — Character delete request from char select screen.</summary>
public sealed class PacketCharDelete : PacketHandler
{
    public PacketCharDelete() : base(0x83, 39) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        string password = buffer.ReadAsciiFixed(30);
        int charIndex = buffer.ReadInt32();
        uint clientIp = buffer.ReadUInt32();
        state.OnCharDelete(charIndex, password);
    }
}

/// <summary>0x95 — Dye response (color selection from dye vat dialog).</summary>
public sealed class PacketDyeResponse : PacketHandler
{
    public PacketDyeResponse() : base(0x95, 9) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint itemSerial = buffer.ReadUInt32();
        ushort hue = buffer.ReadUInt16();
        ushort dyeVatSerial = buffer.ReadUInt16();
        state.OnDyeResponse(itemSerial, hue);
    }
}

/// <summary>0x9A — Prompt response (text input for rune names, house signs, etc.).</summary>
public sealed class PacketPromptResponse : PacketHandler
{
    public PacketPromptResponse() : base(0x9A, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        uint promptId = buffer.ReadUInt32();
        uint type = buffer.ReadUInt32(); // 0=cancel, 1=ok
        string text = "";
        if (type != 0 && buffer.Remaining > 0)
        {
            // Remaining bytes are the response text (ASCII, null-terminated)
            text = buffer.ReadAsciiNull();
        }
        state.OnPromptResponse(serial, promptId, type, text);
    }
}

/// <summary>0x7D — Menu choice response (old-style menus, crafting, etc.).</summary>
public sealed class PacketMenuChoice : PacketHandler
{
    public PacketMenuChoice() : base(0x7D, 13) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        ushort menuId = buffer.ReadUInt16();
        ushort index = buffer.ReadUInt16();
        ushort modelId = buffer.ReadUInt16();
        ushort hue = buffer.ReadUInt16();
        state.OnMenuChoice(serial, menuId, index, modelId);
    }
}

// ==================== Phase 2: Content Features ====================

/// <summary>0x66 — Book page read/write request.</summary>
public sealed class PacketBookPage : PacketHandler
{
    public PacketBookPage() : base(0x66, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        ushort pageCount = buffer.ReadUInt16();

        const int MaxPages = 64;
        const int MaxLinesPerPage = 64;
        var pages = new List<(ushort PageNum, string[] Lines)>();
        int maxPageCount = Math.Min((int)pageCount, MaxPages);
        for (int i = 0; i < maxPageCount && buffer.Remaining >= 4; i++)
        {
            ushort pageNum = buffer.ReadUInt16();
            ushort lineCount = buffer.ReadUInt16();

            if (lineCount == 0xFFFF)
            {
                pages.Add((pageNum, Array.Empty<string>()));
                continue;
            }

            var lines = new List<string>();
            int maxLines = Math.Min((int)lineCount, MaxLinesPerPage);
            for (int j = 0; j < maxLines && buffer.Remaining > 0; j++)
            {
                lines.Add(buffer.ReadAsciiNull());
            }
            pages.Add((pageNum, lines.ToArray()));
        }
        state.OnBookPage(serial, pages);
    }
}

/// <summary>0x93 — Book header change (title/author edit).</summary>
public sealed class PacketBookHeader : PacketHandler
{
    public PacketBookHeader() : base(0x93, 99) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        byte writable = buffer.ReadByte();
        buffer.ReadByte(); // unknown
        ushort pages = buffer.ReadUInt16();
        string title = buffer.ReadAsciiFixed(60);
        string author = buffer.ReadAsciiFixed(30);
        state.OnBookHeader(serial, writable != 0, title.TrimEnd('\0'), author.TrimEnd('\0'));
    }
}

/// <summary>0x71 — Bulletin board message interaction.</summary>
public sealed class PacketBulletinBoard : PacketHandler
{
    public PacketBulletinBoard() : base(0x71, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte subCmd = buffer.ReadByte();
        uint boardSerial = buffer.ReadUInt32();

        switch (subCmd)
        {
            case 3: // Request message list
                state.OnBulletinBoardRequestList(boardSerial);
                break;
            case 4: // Request specific message
                if (buffer.Remaining >= 4)
                {
                    uint msgSerial = buffer.ReadUInt32();
                    state.OnBulletinBoardRequestMessage(boardSerial, msgSerial);
                }
                break;
            case 5: // Post new message
                if (buffer.Remaining >= 4)
                {
                    uint replyTo = buffer.ReadUInt32();
                    byte subjectLen = buffer.Remaining > 0 ? buffer.ReadByte() : (byte)0;
                    string subject = subjectLen > 0 && buffer.Remaining >= subjectLen
                        ? buffer.ReadAsciiFixed(subjectLen).TrimEnd('\0') : "";
                    var bodyLines = new List<string>();
                    byte lineCount = buffer.Remaining > 0 ? buffer.ReadByte() : (byte)0;
                    const int MaxBoardLines = 32;
                    int maxLines = Math.Min((int)lineCount, MaxBoardLines);
                    for (int i = 0; i < maxLines && buffer.Remaining > 0; i++)
                    {
                        byte lineLen = buffer.ReadByte();
                        string line = lineLen > 0 && buffer.Remaining >= lineLen
                            ? buffer.ReadAsciiFixed(lineLen).TrimEnd('\0') : "";
                        bodyLines.Add(line);
                    }
                    state.OnBulletinBoardPost(boardSerial, replyTo, subject, bodyLines.ToArray());
                }
                break;
            case 6: // Delete message
                if (buffer.Remaining >= 4)
                {
                    uint msgSerial = buffer.ReadUInt32();
                    state.OnBulletinBoardDelete(boardSerial, msgSerial);
                }
                break;
        }
    }
}

/// <summary>0x90 — Map detail request (cartography).</summary>
public sealed class PacketMapDetail : PacketHandler
{
    public PacketMapDetail() : base(0x90, 19) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        // Corner coordinates for the visible map area
        ushort x1 = buffer.ReadUInt16();
        ushort y1 = buffer.ReadUInt16();
        ushort x2 = buffer.ReadUInt16();
        ushort y2 = buffer.ReadUInt16();
        ushort width = buffer.ReadUInt16();
        ushort height = buffer.ReadUInt16();
        state.OnMapDetail(serial);
    }
}

/// <summary>0x56 — Map pin edit (add/remove/move pins on a map item).</summary>
public sealed class PacketMapPinEdit : PacketHandler
{
    public PacketMapPinEdit() : base(0x56, 11) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        byte action = buffer.ReadByte(); // 1=add, 5=toggle edit, 6=insert, 7=move
        byte pinId = buffer.ReadByte();
        ushort x = buffer.ReadUInt16();
        ushort y = buffer.ReadUInt16();
        state.OnMapPinEdit(serial, action, pinId, x, y);
    }
}

// ==================== Phase 3: Client Compatibility ====================

/// <summary>0xD9 — Hardware info (client sends system specs). Log only.</summary>
public sealed class PacketHardwareInfo : PacketHandler
{
    // 0xD9 is fixed length 0x0102 (258 bytes data). Size=268 in PacketDefinitions but
    // payload after opcode byte = 267 bytes. We handle all remaining as info.
    public PacketHardwareInfo() : base(0xD9, 268) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        // Silently accept — hardware info is for logging/analytics only
        state.OnHardwareInfo();
    }
}

/// <summary>0xA4 — System info (machine name, OS, etc.). Log only.</summary>
public sealed class PacketSystemInfo : PacketHandler
{
    public PacketSystemInfo() : base(0xA4, 149) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        // Silently accept — system info is informational only
        state.OnSystemInfo();
    }
}

/// <summary>0xBE — Assist version (Razor/UOAssist version report).</summary>
public sealed class PacketAssistVersion : PacketHandler
{
    public PacketAssistVersion() : base(0xBE, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint version = buffer.Remaining >= 4 ? buffer.ReadUInt32() : 0;
        state.OnAssistVersion(version);
    }
}

/// <summary>
/// 0xAC — Gump Value Input response (client → server). Matches the
/// reply for the 0xAB <c>PacketGumpValueInput</c> opened by the script
/// <c>INPDLG</c> verb.
///
/// Wire format (from Source-X receive.cpp:2009):
/// <code>
///   byte   cmd       = 0xAC
///   word   length    (variable)
///   dword  serial    (target object UID, echoed from 0xAB)
///   word   context   (echoed CLIMODE / discriminator)
///   byte   action    (1 = OK, 0 = CANCEL)
///   word   textLen   (chars in <c>text</c>, NOT including null)
///   bytes  text      (ASCII)
/// </code>
/// </summary>
public sealed class PacketGumpTextEntry : PacketHandler
{
    public PacketGumpTextEntry() : base(0xAC, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.ReadUInt32();
        ushort context = buffer.ReadUInt16();
        byte action = buffer.ReadByte();
        ushort textLen = buffer.Remaining >= 2 ? buffer.ReadUInt16() : (ushort)0;
        string text = textLen > 0 && buffer.Remaining >= textLen
            ? buffer.ReadAsciiFixed(textLen).TrimEnd('\0') : "";
        state.OnGumpTextEntry(serial, context, action, text);
    }
}

/// <summary>0x98 — All names request (request names of visible mobiles).</summary>
public sealed class PacketAllNamesReq : PacketHandler
{
    public PacketAllNamesReq() : base(0x98, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint serial = buffer.Remaining >= 4 ? buffer.ReadUInt32() : 0;
        state.OnAllNamesRequest(serial);
    }
}

/// <summary>0xB2 — Chat text message (legacy chat system). Silently accept.</summary>
public sealed class PacketChatText : PacketHandler
{
    public PacketChatText() : base(0xB2, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        // Legacy chat system — silently ignore
    }
}
