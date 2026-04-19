namespace SphereNet.Network.Packets;

/// <summary>
/// Packet opcode definitions and length table. Maps to Source-X CPacketManager.
/// Length of 0 means variable-length (length in bytes 1-2 of packet).
/// Source-X uses per-class expected lengths; this is a condensed table.
/// Derived from legacy g_Packet_Lengths (3.0.0a client) + Source-X handler registrations.
/// </summary>
public static class PacketDefinitions
{
    /// <summary>Packet lengths indexed by opcode. 0 = variable length.</summary>
    private static readonly int[] _lengths = new int[256];

    static PacketDefinitions()
    {
        // Default: all variable
        Array.Fill(_lengths, 0);

        // Fixed-length packets (from Source-X + legacy table)
        _lengths[0x00] = 0x68;  // 104 - Create Character
        _lengths[0x02] = 0x07;  // Move Request
        _lengths[0x03] = 0;     // Speech (variable)
        _lengths[0x04] = 0x02;  // God mode (unused)
        _lengths[0x05] = 0x05;  // Attack Request
        _lengths[0x06] = 0x05;  // Double Click
        _lengths[0x07] = 0x07;  // Pick Up Item
        _lengths[0x08] = 0x0E;  // Drop Item (old)
        _lengths[0x09] = 0x05;  // Single Click
        _lengths[0x0B] = 0x10A; // Damage (old)
        _lengths[0x0C] = 0;     // Edit TileData (variable)
        _lengths[0x0D] = 0x03;
        _lengths[0x0E] = 0;     // variable
        _lengths[0x0F] = 0x3D;
        _lengths[0x10] = 0;     // variable (object info)
        _lengths[0x11] = 0;     // variable (stat info)
        _lengths[0x12] = 0;     // variable (ext cmd)
        _lengths[0x13] = 0x0A;  // Equip Item
        _lengths[0x14] = 0x06;
        _lengths[0x15] = 0x09;
        _lengths[0x16] = 0x01;
        _lengths[0x17] = 0;     // variable
        _lengths[0x1A] = 0;     // variable (object info)
        _lengths[0x1B] = 0x25;  // 37 - Login Confirm
        _lengths[0x1C] = 0;     // variable (speech)
        _lengths[0x1D] = 0x05;  // Delete Object
        _lengths[0x1E] = 0x04;
        _lengths[0x1F] = 0x08;
        _lengths[0x20] = 0x13;  // 19 - Draw Player
        _lengths[0x21] = 0x08;  // Move Reject
        _lengths[0x22] = 0x03;  // Move Ack
        _lengths[0x23] = 0x1A;  // Drag Animation
        _lengths[0x24] = 0x07;  // Open Container
        _lengths[0x25] = 0x14;  // Container Item Update (old)
        _lengths[0x26] = 0x05;
        _lengths[0x27] = 0x02;  // Pickup Failed
        _lengths[0x28] = 0x05;
        _lengths[0x29] = 0x01;  // Drop Ack
        _lengths[0x2A] = 0x05;
        _lengths[0x2B] = 0x02;
        _lengths[0x2C] = 0x02;  // Death Menu
        _lengths[0x2D] = 0x11;
        _lengths[0x2E] = 0x0F;  // Equip Item
        _lengths[0x2F] = 0x0A;  // Combat Swing
        _lengths[0x30] = 0x05;
        _lengths[0x31] = 0x01;
        _lengths[0x32] = 0x02;
        _lengths[0x33] = 0x02;  // Pause Client
        _lengths[0x34] = 0x0A;  // Status Request
        _lengths[0x36] = 0;     // variable
        _lengths[0x37] = 0x08;
        _lengths[0x38] = 0x07;
        _lengths[0x39] = 0x09;
        _lengths[0x3A] = 0;     // variable (Skill Lock)
        _lengths[0x3B] = 0;     // variable (Vendor Buy)
        _lengths[0x3C] = 0;     // variable (content)
        _lengths[0x3F] = 0;     // variable
        _lengths[0x4E] = 0x06;  // Personal Light Level
        _lengths[0x4F] = 0x02;  // Global Light Level
        _lengths[0x54] = 0x0C;  // Sound
        _lengths[0x55] = 0x01;  // Login Complete
        _lengths[0x56] = 0x0B;  // Map Edit
        _lengths[0x5D] = 0x49;  // 73 - Character Select
        _lengths[0x65] = 0x04;  // Weather
        _lengths[0x66] = 0;     // variable (Book Page)
        _lengths[0x6C] = 0x13;  // 19 - Target
        _lengths[0x6D] = 0x03;  // Play Music
        _lengths[0x6E] = 0x0E;  // Char Action
        _lengths[0x6F] = 0;     // variable (Secure Trade)
        _lengths[0x70] = 0x1C;  // Effect
        _lengths[0x71] = 0;     // variable (BBoard)
        _lengths[0x72] = 0x05;  // War Mode
        _lengths[0x73] = 0x02;  // Ping
        _lengths[0x74] = 0;     // variable (Vendor Buy Content)
        _lengths[0x75] = 0x23;  // Char Name
        _lengths[0x76] = 0x10;  // Zone Change
        _lengths[0x77] = 0x11;  // Update Mobile
        _lengths[0x78] = 0;     // variable (Draw Object)
        _lengths[0x7C] = 0;     // variable (Menu)
        _lengths[0x7D] = 0x0D;  // Menu Choice
        _lengths[0x80] = 0x3E;  // 62 - Login Request
        _lengths[0x82] = 0x02;  // Login Denied
        _lengths[0x83] = 0x27;  // Char Delete
        _lengths[0x86] = 0;     // variable (Char List Update)
        _lengths[0x88] = 0x42;  // Open Paperdoll
        _lengths[0x89] = 0;     // variable (Corpse Equip)
        _lengths[0x8B] = 0;     // variable
        _lengths[0x8C] = 0x0B;  // Relay Server
        _lengths[0x8D] = 0;     // variable (Create New)
        _lengths[0x90] = 0x13;  // Map Detail
        _lengths[0x91] = 0x41;  // 65 - Game Server Login
        _lengths[0x93] = 0x63;  // Book Open
        _lengths[0x95] = 0x09;  // Dye Vat
        _lengths[0x97] = 0x02;
        _lengths[0x98] = 0;     // variable (All Names)
        _lengths[0x99] = 0x1A;  // Multi Target
        _lengths[0x9A] = 0;     // variable (Prompt)
        _lengths[0x9B] = 0x0102; // Help Page
        _lengths[0x9E] = 0;     // variable (Sell List)
        _lengths[0x9F] = 0;     // variable (Vendor Sell)
        _lengths[0xA0] = 0x03;  // Server Select
        _lengths[0xA1] = 0x09;  // Stat Update (hits)
        _lengths[0xA2] = 0x09;  // Stat Update (mana)
        _lengths[0xA3] = 0x09;  // Stat Update (stam)
        _lengths[0xA4] = 0x95;  // 149 - System Info
        _lengths[0xA5] = 0;     // variable (Web Link)
        _lengths[0xA6] = 0;     // variable (Scroll)
        _lengths[0xA7] = 0;     // variable (Tip)
        _lengths[0xA8] = 0;     // variable (Server List)
        _lengths[0xA9] = 0;     // variable (Char List)
        _lengths[0xAA] = 0x05;  // Attack Notify
        _lengths[0xAB] = 0;     // variable (Text Input)
        _lengths[0xAC] = 0;     // variable (Gump Input Value)
        _lengths[0xAD] = 0;     // variable (Unicode Speech)
        _lengths[0xAE] = 0;     // variable (Unicode Speech Msg)
        _lengths[0xAF] = 0x0D;  // Death Animation
        _lengths[0xB0] = 0;     // variable (Gump Dialog)
        _lengths[0xB1] = 0;     // variable (Gump Response)
        _lengths[0xB2] = 0;     // variable (Chat Text)
        _lengths[0xB5] = 0x40;  // Chat
        _lengths[0xB6] = 0x09;  // Tooltip Request
        _lengths[0xB7] = 0;     // variable (Tooltip)
        _lengths[0xB8] = 0;     // variable (Profile)
        _lengths[0xB9] = 0x05;  // Feature Enable (5 bytes for 6.0.14.2+)
        _lengths[0xBA] = 0x06;  // Arrow
        _lengths[0xBB] = 0x09;  // Mail Msg
        _lengths[0xBC] = 0x03;  // Season
        _lengths[0xBD] = 0;     // variable (Client Version)
        _lengths[0xBE] = 0;     // variable (Assist Version)
        _lengths[0xBF] = 0;     // variable (Extended Data)
        _lengths[0xC0] = 0x24;  // Effect Extended
        _lengths[0xC1] = 0;     // variable (Cliloc)
        _lengths[0xC2] = 0;     // variable (Prompt Unicode)
        _lengths[0xC7] = 0x31;
        _lengths[0xC8] = 0x02;  // View Range
        _lengths[0xCA] = 0x06;
        _lengths[0xCB] = 0x01;
        _lengths[0xCC] = 0;     // variable (Cliloc Affix)
        _lengths[0xD0] = 0;     // variable (Config File)
        _lengths[0xD1] = 0x02;  // Logout Status
        _lengths[0xD2] = 0x19;
        _lengths[0xD3] = 0;     // variable
        _lengths[0xD4] = 0;     // variable (AOS Book Page)
        _lengths[0xD6] = 0;     // variable (AOS Tooltip Request)
        _lengths[0xD7] = 0;     // variable (Encoded Command)
        _lengths[0xD8] = 0;     // variable (Custom House)
        _lengths[0xD9] = 0x0102; // Hardware Info
        _lengths[0xDA] = 0;     // variable
        _lengths[0xDB] = 0;     // variable
        _lengths[0xDC] = 0x09;  // AOS Tooltip Revision
        _lengths[0xDD] = 0;     // variable (Compressed Gump)
        _lengths[0xDE] = 0;     // variable
        _lengths[0xDF] = 0;     // variable (Buff System)
        _lengths[0xE0] = 0;     // variable (Bug Report)
        _lengths[0xE1] = 0;     // variable (KR/EC Client Type)
        _lengths[0xE2] = 0x0A;  // New Animation
        _lengths[0xE3] = 0;     // variable (KR Encryption)
        _lengths[0xE5] = 0;     // variable
        _lengths[0xE6] = 0x05;
        _lengths[0xE7] = 0x0C;
        _lengths[0xE8] = 0x0D;  // Highlight UI Remove
        _lengths[0xE9] = 0;     // variable
        _lengths[0xEA] = 0;     // variable
        _lengths[0xEB] = 0;     // variable (Use Hotbar)
        _lengths[0xEC] = 0;     // variable (Equip Macro)
        _lengths[0xED] = 0;     // variable (Unequip Macro)
        _lengths[0xEF] = 0x15;  // 21 - KR/EC Login Seed
        _lengths[0xF0] = 0;     // variable (New Movement Request)
        _lengths[0xF1] = 0x09;  // Time Sync
        _lengths[0xF2] = 0;     // variable (New Movement Response)
        _lengths[0xF3] = 0x18;  // 24 - SA Object Info
        _lengths[0xF4] = 0;     // variable (Crash Report)
        _lengths[0xF5] = 0x15;  // 21 - New Map
        _lengths[0xF6] = 0;     // variable (Boat Smooth Move)
        _lengths[0xF7] = 0;     // variable (SA Container Content)
        _lengths[0xF8] = 0x6A;  // 106 - Create Character (HS)
        _lengths[0xF9] = 0;     // variable (Global Chat)
        _lengths[0xFA] = 0x01;  // Ultima Store Button
        _lengths[0xFB] = 0x02;  // Public House Content
    }

    public static int GetPacketLength(byte opcode) => _lengths[opcode];
    public static bool IsVariableLength(byte opcode) => _lengths[opcode] == 0;

    /// <summary>
    /// Version-aware packet length lookup for incoming packets whose size
    /// changed between client versions (0x08 Drop Item, 0x00 Create Character).
    /// </summary>
    public static int GetPacketLength(byte opcode, State.NetState? ns)
    {
        if (ns != null)
        {
            // 0x08 Drop Item: 14 bytes pre-6.0.1.7, 15 bytes 6.0.1.7+
            if (opcode == 0x08) return ns.IsClientPost6017 ? 15 : 14;
            // 0x00 Create Character: 104 bytes pre-7.0.18, 106 bytes 7.0.18+
            if (opcode == 0x00) return ns.IsClientPost70180 ? 106 : 104;
        }
        return _lengths[opcode];
    }
}
