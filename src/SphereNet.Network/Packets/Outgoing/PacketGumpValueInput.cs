using SphereNet.Network.Packets;

namespace SphereNet.Network.Packets.Outgoing;

/// <summary>
/// 0xAB — Gump Value Input dialog (server → client). Source-X
/// equivalent: <c>PacketGumpValueInput</c> emitted by
/// <c>CClient::addGumpInputVal</c>. Used by the script <c>INPDLG</c>
/// verb to prompt a property edit on a target object.
///
/// Wire format (matches Source-X send.cpp:3482):
/// <code>
///   byte   cmd       = 0xAB
///   word   length    (variable)
///   dword  serial    (target object UID)
///   word   context   (CLIMODE / discriminator, echoed in 0xAC reply)
///   word   captionLen (chars + 1, including null)
///   bytes  caption   (ASCII, null-terminated, fixed length = captionLen)
///   byte   cancel    (1 = cancellable, 0 = ok-only)
///   byte   style     (INPVAL_STYLE: 0 = NOEDIT, 1 = TEXT, 2 = NUMERIC)
///   dword  maxLength (max chars/value the client should accept)
///   word   descLen   (chars + 1)
///   bytes  descText  (ASCII, null-terminated, fixed length = descLen)
/// </code>
/// </summary>
public sealed class PacketGumpValueInput : PacketWriter
{
    /// <summary>Style hint for the input dialog (matches Source-X INPVAL_STYLE).</summary>
    public enum InputStyle : byte
    {
        NoEdit = 0,
        TextEdit = 1,
        NumericEdit = 2,
    }

    private readonly uint _targetSerial;
    private readonly ushort _context;
    private readonly bool _cancel;
    private readonly InputStyle _style;
    private readonly uint _maxLength;
    private readonly string _caption;
    private readonly string _description;

    public PacketGumpValueInput(
        uint targetSerial,
        ushort context,
        string caption,
        string description,
        uint maxLength,
        InputStyle style = InputStyle.TextEdit,
        bool cancel = true)
        : base(0xAB)
    {
        _targetSerial = targetSerial;
        _context = context;
        _caption = caption ?? "";
        _description = description ?? "";
        _maxLength = maxLength;
        _style = style;
        _cancel = cancel;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64 + _caption.Length + _description.Length);
        buf.WriteUInt32(_targetSerial);
        buf.WriteUInt16(_context);

        int captionLen = Math.Min(_caption.Length + 1, 255);
        buf.WriteUInt16((ushort)captionLen);
        buf.WriteAsciiFixed(_caption, captionLen);

        buf.WriteByte(_cancel ? (byte)1 : (byte)0);
        buf.WriteByte((byte)_style);
        buf.WriteUInt32(_maxLength);

        int descLen = Math.Min(_description.Length + 1, 255);
        buf.WriteUInt16((ushort)descLen);
        buf.WriteAsciiFixed(_description, descLen);

        buf.WriteLengthAt(1);
        return buf;
    }
}
