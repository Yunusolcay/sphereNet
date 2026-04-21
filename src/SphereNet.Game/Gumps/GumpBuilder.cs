using System.Text;

namespace SphereNet.Game.Gumps;

/// <summary>
/// Gump control commands. Maps to GUMPCTL_TYPE enum in Source-X CDialogDef.cpp.
/// </summary>
public enum GumpControl
{
    Button,
    ButtonTileArt,
    Checkbox,
    CheckerTrans,
    CroppedText,
    DCroppedText,
    DHtmlGump,
    DOrigin,
    DText,
    DTextEntry,
    DTextEntryLimited,
    Group,
    GumpPic,
    GumpPicTiled,
    HtmlGump,
    ItemProperty,
    NoClose,
    NoDispose,
    NoMove,
    Page,
    PicInPic,
    Radio,
    ResizePic,
    Text,
    TextEntry,
    TextEntryLimited,
    TilePic,
    TilePicHue,
    Tooltip,
    XmfHtmlGump,
    XmfHtmlGumpColor,
    XmfHtmlTok,
}

/// <summary>
/// Gump builder. Maps to CDialogDef gump layout in Source-X.
/// Constructs a gump layout + text list that can be sent via compressed gump packet (0xDD).
/// </summary>
public sealed class GumpBuilder
{
    private readonly List<string> _layout = [];
    private readonly List<string> _texts = [];
    private readonly uint _serial;
    private readonly uint _gumpId;
    public uint Serial => _serial;
    public uint GumpId => _gumpId;
    public int Width { get; set; }
    public int Height { get; set; }
    public IReadOnlyList<string> Layout => _layout;
    public IReadOnlyList<string> Texts => _texts;

    public GumpBuilder(uint serial, uint gumpId, int width = 300, int height = 300)
    {
        _serial = serial;
        _gumpId = gumpId;
        Width = width;
        Height = height;
    }

    private int AddText(string text)
    {
        int idx = _texts.Count;
        _texts.Add(text);
        return idx;
    }

    // --- Layout commands (match Source-X script keywords) ---

    public GumpBuilder SetPage(int page)
    {
        _layout.Add($"{{ page {page} }}");
        return this;
    }

    public GumpBuilder AddResizePic(int x, int y, int gumpId, int width, int height)
    {
        _layout.Add($"{{ resizepic {x} {y} {gumpId} {width} {height} }}");
        return this;
    }

    public GumpBuilder AddGumpPic(int x, int y, int gumpId, int hue = 0)
    {
        if (hue != 0)
            _layout.Add($"{{ gumppic {x} {y} {gumpId} hue={hue} }}");
        else
            _layout.Add($"{{ gumppic {x} {y} {gumpId} }}");
        return this;
    }

    public GumpBuilder AddGumpPicTiled(int x, int y, int width, int height, int gumpId)
    {
        _layout.Add($"{{ gumppictiled {x} {y} {width} {height} {gumpId} }}");
        return this;
    }

    public GumpBuilder AddTilePic(int x, int y, int tileId)
    {
        _layout.Add($"{{ tilepic {x} {y} {tileId} }}");
        return this;
    }

    public GumpBuilder AddTilePicHue(int x, int y, int tileId, int hue)
    {
        _layout.Add($"{{ tilepichue {x} {y} {tileId} {hue} }}");
        return this;
    }

    public GumpBuilder AddText(int x, int y, int hue, string text)
    {
        int idx = AddText(text);
        _layout.Add($"{{ text {x} {y} {hue} {idx} }}");
        return this;
    }

    public GumpBuilder AddCroppedText(int x, int y, int width, int height, int hue, string text)
    {
        int idx = AddText(text);
        _layout.Add($"{{ croppedtext {x} {y} {width} {height} {hue} {idx} }}");
        return this;
    }

    public GumpBuilder AddButton(int x, int y, int normalId, int pressedId, int buttonId, int type = 1, int page = 0)
    {
        _layout.Add($"{{ button {x} {y} {normalId} {pressedId} {type} {page} {buttonId} }}");
        return this;
    }

    public GumpBuilder AddCheckbox(int x, int y, int uncheckedId, int checkedId, bool initialState, int switchId)
    {
        _layout.Add($"{{ checkbox {x} {y} {uncheckedId} {checkedId} {(initialState ? 1 : 0)} {switchId} }}");
        return this;
    }

    public GumpBuilder AddRadio(int x, int y, int uncheckedId, int checkedId, bool initialState, int switchId)
    {
        _layout.Add($"{{ radio {x} {y} {uncheckedId} {checkedId} {(initialState ? 1 : 0)} {switchId} }}");
        return this;
    }

    public GumpBuilder AddTextEntry(int x, int y, int width, int height, int hue, int entryId, string initialText)
    {
        int idx = AddText(initialText);
        _layout.Add($"{{ textentry {x} {y} {width} {height} {hue} {entryId} {idx} }}");
        return this;
    }

    /// <summary>
    /// textentrylimited variant — emits the `{ textentrylimited ... limit }`
    /// layout token so the client caps the input length at <paramref name="limit"/>.
    /// Source-X CDialogDef writes this as control id GUMPCTL_TEXTENTRYLIMITED.
    /// </summary>
    public GumpBuilder AddTextEntryLimited(int x, int y, int width, int height, int hue, int entryId, string initialText, int limit)
    {
        int idx = AddText(initialText);
        // Older 2D clients read the trailing 8th token as the cap. Sphere's
        // CDialogDef serialises the same shape; mortechUO admin INPDLGs rely
        // on it to keep INPDLG NAME, BODY, COLOR, … inside their script
        // limits.
        if (limit < 0) limit = 0;
        _layout.Add($"{{ textentrylimited {x} {y} {width} {height} {hue} {entryId} {idx} {limit} }}");
        return this;
    }

    public GumpBuilder AddHtmlGump(int x, int y, int width, int height, string html, bool hasBackground, bool hasScrollbar)
    {
        int idx = AddText(html);
        _layout.Add($"{{ htmlgump {x} {y} {width} {height} {idx} {(hasBackground ? 1 : 0)} {(hasScrollbar ? 1 : 0)} }}");
        return this;
    }

    public GumpBuilder AddXmfHtmlGump(int x, int y, int width, int height, uint clilocId, bool hasBackground, bool hasScrollbar)
    {
        _layout.Add($"{{ xmfhtmlgump {x} {y} {width} {height} {clilocId} {(hasBackground ? 1 : 0)} {(hasScrollbar ? 1 : 0)} }}");
        return this;
    }

    public GumpBuilder AddXmfHtmlGumpColor(int x, int y, int width, int height, uint clilocId, bool hasBackground, bool hasScrollbar, int color)
    {
        _layout.Add($"{{ xmfhtmlgumpcolor {x} {y} {width} {height} {clilocId} {(hasBackground ? 1 : 0)} {(hasScrollbar ? 1 : 0)} {color} }}");
        return this;
    }

    public GumpBuilder AddCheckerTrans(int x, int y, int width, int height)
    {
        _layout.Add($"{{ checkertrans {x} {y} {width} {height} }}");
        return this;
    }

    public GumpBuilder AddGroup(int group)
    {
        _layout.Add($"{{ group {group} }}");
        return this;
    }

    public GumpBuilder SetNoClose()
    {
        _layout.Add("{ noclose }");
        return this;
    }

    public GumpBuilder SetNoDispose()
    {
        _layout.Add("{ nodispose }");
        return this;
    }

    public GumpBuilder SetNoMove()
    {
        _layout.Add("{ nomove }");
        return this;
    }

    public GumpBuilder AddItemProperty(uint serial)
    {
        _layout.Add($"{{ itemproperty {serial} }}");
        return this;
    }

    /// <summary>Build the full layout string for the network packet.</summary>
    public string BuildLayoutString()
    {
        var sb = new StringBuilder();
        foreach (var line in _layout)
            sb.Append(line);
        return sb.ToString();
    }
}

/// <summary>
/// Gump response data from the client (0xB1 packet).
/// </summary>
public sealed class GumpResponse
{
    public uint Serial { get; init; }
    public uint GumpId { get; init; }
    public uint ButtonId { get; init; }
    public int[] Switches { get; init; } = [];
    public Dictionary<int, string> TextEntries { get; init; } = [];
}
