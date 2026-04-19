using SphereNet.Network.Packets;

namespace SphereNet.Network.Packets.Outgoing;

/// <summary>Entry describing a single menu item for the 0x7C packet.</summary>
public record MenuItemEntry(ushort ModelId, ushort Hue, string Text);

/// <summary>0x7C — Item List Menu (old-style menu display).</summary>
public sealed class PacketMenuDisplay : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _menuId;
    private readonly string _question;
    private readonly IReadOnlyList<MenuItemEntry> _items;

    public PacketMenuDisplay(uint serial, ushort menuId, string question, IReadOnlyList<MenuItemEntry> items)
        : base(0x7C)
    {
        _serial = serial;
        _menuId = menuId;
        _question = question;
        _items = items;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(256);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_menuId);

        // Question / title
        byte qLen = (byte)Math.Min(_question.Length, 255);
        buf.WriteByte(qLen);
        buf.WriteAsciiFixed(_question, qLen);

        // Items
        buf.WriteByte((byte)Math.Min(_items.Count, 255));
        foreach (var item in _items)
        {
            buf.WriteUInt16(item.ModelId);
            buf.WriteUInt16(item.Hue);
            byte nameLen = (byte)Math.Min(item.Text.Length, 255);
            buf.WriteByte(nameLen);
            buf.WriteAsciiFixed(item.Text, nameLen);
        }

        buf.WriteLengthAt(1);
        return buf;
    }
}
