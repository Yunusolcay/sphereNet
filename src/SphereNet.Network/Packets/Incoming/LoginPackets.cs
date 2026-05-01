using SphereNet.Core.Types;
using SphereNet.Network.Packets;

namespace SphereNet.Network.Packets.Incoming;

/// <summary>0x80 — Login request (client → login server).</summary>
public sealed class PacketLoginRequest : PacketHandler
{
    public PacketLoginRequest() : base(0x80, 62) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        string account = buffer.ReadAsciiFixed(30);
        string password = buffer.ReadAsciiFixed(30);
        byte nextLoginKey = buffer.ReadByte();

        state.OnLoginRequest(account, password);
    }
}

/// <summary>0x91 — Game server login (client → game server after relay).</summary>
public sealed class PacketGameLogin : PacketHandler
{
    public PacketGameLogin() : base(0x91, 65) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint authId = buffer.ReadUInt32();
        string account = buffer.ReadAsciiFixed(30);
        string password = buffer.ReadAsciiFixed(30);

        state.OnGameLogin(account, password, authId);
    }
}

/// <summary>0xF8 — Create Character (HS, 7.0+ clients). 106 bytes.</summary>
public sealed class PacketCreateCharacterHS : PacketHandler
{
    public PacketCreateCharacterHS() : base(0xF8, 106) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadUInt32(); // pattern1
        buffer.ReadUInt32(); // pattern2
        buffer.ReadByte();   // pattern3
        string charName = buffer.ReadAsciiFixed(30);

        buffer.ReadBytes(2); // unknown
        buffer.ReadUInt32(); // client flags
        buffer.ReadBytes(8); // unknown
        buffer.ReadByte();   // profession
        buffer.ReadBytes(15); // unknown

        byte genderRace = buffer.ReadByte();
        byte str = buffer.ReadByte();
        byte dex = buffer.ReadByte();
        byte intl = buffer.ReadByte();

        var skills = new (byte Id, byte Value)[4];
        for (int i = 0; i < 4; i++)
        {
            skills[i].Id = buffer.ReadByte();
            skills[i].Value = buffer.ReadByte();
        }

        ushort skinHue = buffer.ReadUInt16();
        ushort hairStyle = buffer.ReadUInt16();
        ushort hairHue = buffer.ReadUInt16();
        ushort beardStyle = buffer.ReadUInt16();
        ushort beardHue = buffer.ReadUInt16();

        bool female = (genderRace % 2) != 0;

        state.OnCharCreate(new CharCreateInfo
        {
            Name = charName,
            Female = female,
            Str = str, Dex = dex, Int = intl,
            SkinHue = skinHue,
            HairStyle = hairStyle, HairHue = hairHue,
            BeardStyle = beardStyle, BeardHue = beardHue,
            Skills = skills,
        });
    }
}

/// <summary>0x00 — Create Character (old clients). 104 bytes.</summary>
public sealed class PacketCreateCharacter : PacketHandler
{
    public PacketCreateCharacter() : base(0x00, 104) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadUInt32(); // pattern1
        buffer.ReadUInt32(); // pattern2
        buffer.ReadByte();   // pattern3
        string charName = buffer.ReadAsciiFixed(30);

        buffer.ReadBytes(2); // unknown
        buffer.ReadUInt32(); // client flags
        buffer.ReadBytes(8); // unknown
        buffer.ReadByte();   // profession
        buffer.ReadBytes(15); // unknown

        byte genderRace = buffer.ReadByte();
        byte str = buffer.ReadByte();
        byte dex = buffer.ReadByte();
        byte intl = buffer.ReadByte();

        var skills = new (byte Id, byte Value)[3];
        for (int i = 0; i < 3; i++)
        {
            skills[i].Id = buffer.ReadByte();
            skills[i].Value = buffer.ReadByte();
        }

        ushort skinHue = buffer.ReadUInt16();
        ushort hairStyle = buffer.ReadUInt16();
        ushort hairHue = buffer.ReadUInt16();
        ushort beardStyle = buffer.ReadUInt16();
        ushort beardHue = buffer.ReadUInt16();

        bool female = (genderRace % 2) != 0;

        state.OnCharCreate(new CharCreateInfo
        {
            Name = charName,
            Female = female,
            Str = str, Dex = dex, Int = intl,
            SkinHue = skinHue,
            HairStyle = hairStyle, HairHue = hairHue,
            BeardStyle = beardStyle, BeardHue = beardHue,
            Skills = skills,
        });
    }
}

/// <summary>0x5D — Character select.</summary>
public sealed class PacketCharSelect : PacketHandler
{
    public PacketCharSelect() : base(0x5D, 73) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadUInt32(); // pattern1
        string charName = buffer.ReadAsciiFixed(30);
        buffer.ReadUInt16(); // unknown
        uint clientFlag = buffer.ReadUInt32();
        buffer.ReadUInt32(); // pattern2
        uint loginCount = buffer.ReadUInt32();
        buffer.ReadBytes(16); // padding
        int slotIndex = buffer.ReadInt32();
        buffer.ReadBytes(4); // clientIP

        state.OnCharSelect(slotIndex, charName);
    }
}

/// <summary>0x73 — Ping request.</summary>
public sealed class PacketPing : PacketHandler
{
    public PacketPing() : base(0x73, 2) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte seq = buffer.ReadByte();
        state.SendPing(seq);
    }
}

/// <summary>0x02 — Move request.</summary>
public sealed class PacketMoveRequest : PacketHandler
{
    public PacketMoveRequest() : base(0x02, 7) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte dir = buffer.ReadByte();
        byte seq = buffer.ReadByte();
        uint fastWalkKey = buffer.ReadUInt32();

        state.OnMoveRequest(dir, seq, fastWalkKey);
    }
}

/// <summary>0x03 — ASCII speech request.</summary>
public sealed class PacketSpeechRequest : PacketHandler
{
    public PacketSpeechRequest() : base(0x03, -1) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte type = buffer.ReadByte();
        ushort hue = buffer.ReadUInt16();
        ushort font = buffer.ReadUInt16();
        string text = buffer.ReadAsciiNull();

        state.OnSpeech(type, hue, font, text);
    }
}

/// <summary>0x05 — Attack request.</summary>
public sealed class PacketAttackRequest : PacketHandler
{
    public PacketAttackRequest() : base(0x05, 5) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        uint targetUid = buffer.ReadUInt32();
        state.OnAttackRequest(targetUid);
    }
}

/// <summary>0x72 — War mode toggle.</summary>
public sealed class PacketWarMode : PacketHandler
{
    public PacketWarMode() : base(0x72, 5) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte warMode = buffer.ReadByte();
        buffer.ReadBytes(3); // unknown
        state.OnWarMode(warMode != 0);
    }
}

/// <summary>0xA0 — Server select (client picks a server from the list).</summary>
public sealed class PacketServerSelect : PacketHandler
{
    public PacketServerSelect() : base(0xA0, 3) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        ushort serverIndex = buffer.ReadUInt16();
        state.OnServerSelect(serverIndex);
    }
}

/// <summary>0x12 — Text command (skill use, spell cast, etc.).</summary>
public sealed class PacketTextCommand : PacketHandler
{
    public PacketTextCommand() : base(0x12, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        byte type = buffer.ReadByte();
        string command = buffer.ReadAsciiNull();
        state.OnTextCommand(type, command);
    }
}

/// <summary>0x3A — Skill lock change.</summary>
public sealed class PacketSkillLock : PacketHandler
{
    public PacketSkillLock() : base(0x3A, 0) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        ushort skillId = buffer.ReadUInt16();
        byte lockState = buffer.ReadByte();
        state.OnTextCommand(0xFF, $"SKILLLOCK {skillId} {lockState}");
    }
}

/// <summary>0x9B — Help request (client → server). Client presses the help button.</summary>
public sealed class PacketHelpRequest : PacketHandler
{
    public PacketHelpRequest() : base(0x9B, 258) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        state.OnHelpRequest();
    }
}

/// <summary>0x22 — Resync request (client → server). Client sends this when desynced.</summary>
public sealed class PacketResyncRequest : PacketHandler
{
    public PacketResyncRequest() : base(0x22, 3) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadByte(); // seq
        buffer.ReadByte(); // notoriety (ignored from client)
        state.OnResyncRequest();
    }
}

/// <summary>0xD1 — Logout request (client → server). Sent when the player clicks
/// "Return to character select" in the paperdoll. Server must reply with an
/// accept (0xD1 + 0x01) so the client actually leaves the world.</summary>
public sealed class PacketLogoutRequest : PacketHandler
{
    public PacketLogoutRequest() : base(0xD1, 2) { }

    public override void OnReceive(PacketBuffer buffer, State.NetState state)
    {
        buffer.ReadByte(); // 0x00 from client (request)
        state.OnLogoutRequest();
    }
}
