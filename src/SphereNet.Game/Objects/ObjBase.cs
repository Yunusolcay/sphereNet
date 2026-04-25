using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Scripting.Variables;

namespace SphereNet.Game.Objects;

/// <summary>
/// Base class for all world objects (items and characters).
/// Maps directly to CObjBase in Source-X.
/// </summary>
public abstract class ObjBase : IScriptObj, ITimedObject, IEntity
{
    public static Action<string>? OnNameChangeWarning;

    private Serial _uid;
    private Guid _uuid = Guid.CreateVersion7();
    private string _name = "";
    private Point3D _position;
    private ushort _baseId;
    private Color _hue;
    private ObjAttributes _attr;
    private readonly VarMap _tags = new();
    private readonly Dictionary<ComponentType, IComponent> _components = [];

    /// <summary>Resolve the GameWorld instance (set at startup).</summary>
    public static Func<World.GameWorld>? ResolveWorld;

    // --- Dirty tracking for delta-based view updates ---
    private DirtyFlag _dirty;
    private Action<ObjBase>? _dirtyNotify;

    public DirtyFlag DirtyFlags => _dirty;
    public bool IsDirty => _dirty != DirtyFlag.None;

    /// <summary>Mark one or more properties as changed.</summary>
    public void MarkDirty(DirtyFlag flags)
    {
        var prev = _dirty;
        _dirty |= flags;
        if (prev == DirtyFlag.None && _dirty != DirtyFlag.None)
            _dirtyNotify?.Invoke(this);
    }

    /// <summary>Read and reset dirty flags (consume).</summary>
    public DirtyFlag ConsumeDirty()
    {
        var d = _dirty;
        _dirty = DirtyFlag.None;
        return d;
    }

    /// <summary>Set the callback invoked on clean→dirty transition (used by GameWorld).</summary>
    public void SetDirtyNotify(Action<ObjBase>? notify) => _dirtyNotify = notify;

    public Serial Uid => _uid;
    public ref Serial UidRef => ref _uid;

    public Guid Uuid
    {
        get => _uuid;
        set => _uuid = value;
    }

    public string Name
    {
        get => _name;
        set
        {
            string newName = value ?? "";
            if (IsChar && _name.Length > 0 && !_name.Equals(newName, StringComparison.Ordinal))
                OnNameChangeWarning?.Invoke(
                    $"0x{_uid.Value:X8} '{_name}' -> '{newName}' via Name setter");
            _name = newName;
            MarkDirty(DirtyFlag.Name);
        }
    }

    public Point3D Position
    {
        get => _position;
        set { _position = value; MarkDirty(DirtyFlag.Position); }
    }

    public short X => _position.X;
    public short Y => _position.Y;
    public sbyte Z => _position.Z;
    public byte MapIndex => _position.Map;

    public ushort BaseId
    {
        get => _baseId;
        set { _baseId = value; MarkDirty(DirtyFlag.Body); }
    }

    public Color Hue
    {
        get => _hue;
        set { _hue = value; MarkDirty(DirtyFlag.Hue); }
    }

    public ObjAttributes Attributes
    {
        get => _attr;
        set => _attr = value;
    }

    public VarMap Tags => _tags;

    /// <summary>Set a tag value on this object.</summary>
    public void SetTag(string key, string value) => _tags.Set(key, value);

    /// <summary>Get a tag value, returning true if found.</summary>
    public bool TryGetTag(string key, out string? value)
    {
        value = _tags.Get(key);
        return value != null;
    }

    /// <summary>Remove a tag.</summary>
    public void RemoveTag(string key) => _tags.Remove(key);

    public bool IsAttr(ObjAttributes flag) => (_attr & flag) != 0;
    public void SetAttr(ObjAttributes flag) => _attr |= flag;
    public void ClearAttr(ObjAttributes flag) => _attr &= ~flag;

    public bool IsItem => _uid.IsItem;
    public bool IsChar => _uid.IsChar;

    public abstract bool IsDeleted { get; }

    // --- IScriptObj ---

    public virtual string GetName() => _name;

    public virtual bool TryGetProperty(string key, out string value)
    {
        value = "";
        switch (key.ToUpperInvariant())
        {
            case "UID": value = $"0{_uid.Value:X}"; return true;
            case "UUID": value = _uuid.ToString("D"); return true;
            case "NAME": value = _name; return true;
            case "P": value = _position.ToString(); return true;
            case "X": value = _position.X.ToString(); return true;
            case "Y": value = _position.Y.ToString(); return true;
            case "Z": value = _position.Z.ToString(); return true;
            case "MAP": value = _position.Map.ToString(); return true;
            // Sphere scripts also access P as a sub-object: <P.X> / <P.Y>
            // / <P.Z> / <P.M>. d_admin uses <REF1.P.X> in the
            // map-region lookup row, so without these keys the world
            // map call collapses to "Serv.Map(,,0,).Region.Name".
            case "P.X": value = _position.X.ToString(); return true;
            case "P.Y": value = _position.Y.ToString(); return true;
            case "P.Z": value = _position.Z.ToString(); return true;
            case "P.M":
            case "P.MAP": value = _position.Map.ToString(); return true;
            case "COLOR": value = _hue.Value.ToString(); return true;
            case "ID": value = $"0{_baseId:X}"; return true;
            case "ATTR": value = ((uint)_attr).ToString(); return true;
            case "TAGCOUNT": value = _tags.Count.ToString(); return true;
            case "TIMER":
            {
                long t = Timeout;
                if (t <= 0) { value = "-1"; return true; }
                long remaining = (t - Environment.TickCount64) / 1000;
                value = remaining > 0 ? remaining.ToString() : "0";
                return true;
            }
        }

        // Map point properties: TERRAIN, STATICS, REGION, ROOM, SECTOR
        if (TryGetMapPointProperty(key.ToUpperInvariant(), out value))
            return true;

        // TAGAT.<index>.KEY / .VAL — ordered access to the object's tag
        // dictionary, mirroring Source-X CVarDefMap. d_SphereAdmin_PlayerTags
        // walks "For x 0 <Eval <TagCount>-1>" and reads
        // <Tagat.<Local.x>.key> / .val to render the editable list.
        if (key.StartsWith("TAGAT.", StringComparison.OrdinalIgnoreCase))
        {
            string rest = key[6..];
            int dot = rest.IndexOf('.');
            string idxStr = dot >= 0 ? rest[..dot] : rest;
            string field = dot >= 0 ? rest[(dot + 1)..] : "";
            if (int.TryParse(idxStr, out int idx) && idx >= 0)
            {
                int n = 0;
                foreach (var pair in _tags.GetAll())
                {
                    if (n == idx)
                    {
                        if (field.Equals("KEY", StringComparison.OrdinalIgnoreCase))
                        { value = pair.Key; return true; }
                        if (field.Equals("VAL", StringComparison.OrdinalIgnoreCase) ||
                            field.Equals("VALUE", StringComparison.OrdinalIgnoreCase) ||
                            field.Length == 0)
                        { value = pair.Value ?? ""; return true; }
                    }
                    n++;
                }
                value = "";
                return true;
            }
        }

        if (key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("TAG0.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DTAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DTAG0.", StringComparison.OrdinalIgnoreCase))
        {
            int dotIdx = key.IndexOf('.');
            string tagKey = dotIdx >= 0 ? key[(dotIdx + 1)..] : "";
            value = _tags.Get(tagKey) ?? "0";
            return true;
        }
        if (key.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase))
        {
            int dotIdx = key.IndexOf('.');
            string tagKey = dotIdx >= 0 ? key[(dotIdx + 1)..] : "";
            // Source-X: CTag lives on CClient, not CChar. Read from the
            // character's client-session storage; anything else (items,
            // offline characters) has no CTag, return "0".
            value = (this is Characters.Character ch ? ch.CTags.Get(tagKey) : null) ?? "0";
            return true;
        }

        // ISTEVENT.defname / ISEVENT.defname
        if (key.StartsWith("ISTEVENT.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ISEVENT.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = key.IndexOf('.');
            string evName = key[(dot + 1)..];
            var checkRid = ResourceId.FromString(evName, Core.Enums.ResType.Events);
            List<ResourceId>? events = null;
            if (this is Characters.Character evCh) events = evCh.Events;
            else if (this is Items.Item evIt) events = evIt.Events;
            value = events != null && events.Contains(checkRid) ? "1" : "0";
            return true;
        }

        return false;
    }

    public virtual bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        // Source-X compatibility: a bare TAG/CTAG/DTAG command with a dotted
        // suffix and no argument clears that entry.
        // Examples:
        //   CTAG0.NPCSELECTED   -> remove client-session tag NPCSELECTED
        //   TAG.MYFLAG          -> remove persistent tag MYFLAG
        string trimmedKey = key.Trim();
        if (string.IsNullOrEmpty(args))
        {
            if (trimmedKey.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase) ||
                trimmedKey.StartsWith("TAG0.", StringComparison.OrdinalIgnoreCase) ||
                trimmedKey.StartsWith("DTAG.", StringComparison.OrdinalIgnoreCase) ||
                trimmedKey.StartsWith("DTAG0.", StringComparison.OrdinalIgnoreCase))
            {
                int dotIdx = trimmedKey.IndexOf('.');
                string tagKey = dotIdx >= 0 ? trimmedKey[(dotIdx + 1)..].Trim() : "";
                if (tagKey.Length > 0)
                    _tags.Remove(tagKey);
                return true;
            }
            if (trimmedKey.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
                trimmedKey.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
                trimmedKey.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase) ||
                trimmedKey.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase))
            {
                int dotIdx = trimmedKey.IndexOf('.');
                string tagKey = dotIdx >= 0 ? trimmedKey[(dotIdx + 1)..].Trim() : "";
                if (tagKey.Length > 0 && this is Characters.Character chClear)
                    chClear.CTags.Remove(tagKey);
                return true;
            }
        }

        switch (key.ToUpperInvariant())
        {
            case "SHOW":
                source.SysMessage($"{GetName()} [0x{Uid.Value:X8}] P={Position.X},{Position.Y},{Position.Z},{Position.Map}");
                return true;
            case "SAY":
            case "EMOTE":
            case "SYSMESSAGE":
            case "SYSMESSAGEF":
                source.SysMessage(args);
                return true;
            case "SYSMESSAGEUA":
            {
                // Format: <hue>,<type>,<font>,<lang>, <text>
                int firstSpace = args.IndexOf(' ');
                if (firstSpace < 0) { source.SysMessage(args); return true; }
                string paramsPart = args[..firstSpace];
                string text = args[(firstSpace + 1)..].TrimStart(' ', ',');
                string[] parms = paramsPart.Split(',');
                ushort hue = 0x0035;
                if (parms.Length > 0 && ushort.TryParse(parms[0], out ushort h)) hue = h;
                source.SysMessage(text, hue);
                return true;
            }
            case "TAG":
                int eqSign = args.IndexOf('=');
                if (eqSign > 0)
                {
                    string tagK = args[..eqSign].Trim();
                    string tagV = args[(eqSign + 1)..].Trim();
                    _tags.Set(tagK, tagV);
                }
                return true;
            case "TAG.REMOVE":
                _tags.Remove(args.Trim());
                return true;
            case "TAGLIST":
            {
                // List all tags on this object to the console
                foreach (var (k, v) in _tags.GetAll())
                    source.SysMessage($"TAG.{k} = {v}");
                return true;
            }
            case "EVENTS":
            {
                if (this is Characters.Character ch)
                    return ApplyEventsCommand(ch.Events, args);
                if (this is Items.Item item)
                    return ApplyEventsCommand(item.Events, args);
                return true;
            }
            case "TRIGGER":
                return true;
            case "REMOVE":
                if (this is Items.Item delItem) delItem.Delete();
                else if (this is Characters.Character delCh) delCh.Delete();
                return true;
            case "TIMER":
                if (long.TryParse(args, out long timerVal))
                    SetTimeout(Environment.TickCount64 + timerVal * 1000);
                return true;
            case "FIX":
                // Re-seat on the terrain (stub — just keeps current Z)
                return true;
            case "SOUND":
                // Sound effect — handled by the callback if set
                if (ushort.TryParse(args, out ushort _))
                    return true; // Sound playback wired at GameClient level
                return true;
            case "EFFECT":
                // Visual effect — handled by the callback if set
                return true;
        }
        return false;
    }

    public virtual bool TrySetProperty(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "NAME":
                if (IsChar && _name.Length > 0 && !_name.Equals(value, StringComparison.Ordinal))
                    OnNameChangeWarning?.Invoke(
                        $"0x{_uid.Value:X8} '{_name}' -> '{value}' via TrySetProperty");
                _name = value;
                return true;
            case "COLOR":
            case "HUE":
                {
                    string v = value.Trim();
                    if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        ushort.TryParse(v.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hexHue))
                    {
                        _hue = new Color(hexHue);
                    }
                    else if (v.StartsWith('0') && v.Length > 1 &&
                             ushort.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out ushort legacyHexHue))
                    {
                        _hue = new Color(legacyHexHue);
                    }
                    else if (ushort.TryParse(v, out ushort hv))
                    {
                        _hue = new Color(hv);
                    }
                    else if (Definitions.DefinitionLoader.TryGetDefValue(v, out string rangeText))
                    {
                        // Resolve defname color ranges like colors_skin → {1002 1058}
                        _hue = new Color(ResolveRandomHueRange(rangeText));
                    }
                }
                return true;
            case "P":
                var parts = value.Split(',');
                if (parts.Length >= 2
                    && short.TryParse(parts[0], out short px)
                    && short.TryParse(parts[1], out short py))
                {
                    sbyte pz = parts.Length > 2 && sbyte.TryParse(parts[2], out sbyte tz) ? tz : (sbyte)0;
                    byte pm = parts.Length > 3 && byte.TryParse(parts[3], out byte tm) ? tm : (byte)0;
                    var target = new Point3D(px, py, pz, pm);
                    // Route through the world so the object moves
                    // between sectors. A bare `_position = ...` would
                    // leave the object registered in the old sector
                    // and invisible to BroadcastNearby at the new spot.
                    var world = ResolveWorld?.Invoke();
                    if (world != null)
                    {
                        if (this is Characters.Character moveCh)
                            world.MoveCharacter(moveCh, target);
                        else if (this is Items.Item moveIt)
                            world.PlaceItem(moveIt, target);
                        else
                            _position = target;
                    }
                    else
                    {
                        _position = target;
                    }
                }
                return true;
            case "ATTR":
                _attr = (ObjAttributes)ParseHexOrDecUInt(value);
                return true;
            case "EVENTS":
                if (this is Characters.Character ch)
                    return SetEventsList(ch.Events, value);
                if (this is Items.Item item)
                    return SetEventsList(item.Events, value);
                return true;
        }

        // TAG/DTAG — persistent on the object (saved). CTAG/CTAG0 —
        // client-session on characters only (cleared on disconnect,
        // mirroring Source-X CClient::m_TagDefs). DTAG is just the
        // decimal-read convention of TAG; it shares TAG's storage.
        if (key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("TAG0.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DTAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DTAG0.", StringComparison.OrdinalIgnoreCase))
        {
            int dotIdx = key.IndexOf('.');
            string tagKey = dotIdx >= 0 ? key[(dotIdx + 1)..] : "";
            _tags.Set(tagKey, value);
            return true;
        }
        if (key.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase))
        {
            int dotIdx = key.IndexOf('.');
            string tagKey = dotIdx >= 0 ? key[(dotIdx + 1)..] : "";
            if (this is Characters.Character ch)
                ch.CTags.Set(tagKey, value);
            // Silently no-op for non-character objects (items, etc.) —
            // CTAG only makes sense on an online character's client.
            return true;
        }

        return false;
    }

    public virtual TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
    {
        return TriggerResult.Default;
    }

    // --- ITimedObject ---

    private long _timeout;
    private bool _isSleeping;

    public long Timeout => _timeout;
    public bool IsSleeping => _isSleeping;

    public void SetTimeout(long timeoutMs) => _timeout = timeoutMs;
    public void GoSleep() => _isSleeping = true;
    public void GoAwake() => _isSleeping = false;

    public abstract bool OnTick();

    // --- IEntity (ECS) ---

    public void SubscribeComponent(ComponentType type, IComponent component)
    {
        _components[type] = component;
    }

    public void RemoveComponent(ComponentType type) => _components.Remove(type);

    public IComponent? GetComponent(ComponentType type) =>
        _components.GetValueOrDefault(type);

    public T? GetComponent<T>(ComponentType type) where T : class, IComponent =>
        _components.GetValueOrDefault(type) as T;

    public bool HasComponent(ComponentType type) => _components.ContainsKey(type);

    // --- Initialization ---

    public void SetUid(Serial uid) => _uid = uid;

    public override string ToString() => $"{GetName()} (0x{_uid.Value:X8})";

    /// <summary>
    /// Resolve map point properties: TERRAIN, STATICS, REGION, ROOM, SECTOR, ISNEARTYPE.
    /// Provides read-only access to terrain/static tile data at this object's position.
    /// </summary>
    private bool TryGetMapPointProperty(string upper, out string value)
    {
        value = "";
        var world = ResolveWorld?.Invoke();
        if (world == null) return false;

        var pos = Position;

        // --- REGION ---
        if (upper == "REGION")
        {
            value = world.FindRegion(pos)?.Name ?? "";
            return true;
        }
        if (upper.StartsWith("REGION.", StringComparison.Ordinal))
        {
            var region = world.FindRegion(pos);
            if (region != null)
            {
                string sub = upper["REGION.".Length..];
                return region.TryGetProperty(sub, out value);
            }
            value = "0";
            return true;
        }

        // --- ROOM ---
        if (upper == "ROOM")
        {
            value = world.FindRoom(pos)?.Name ?? "";
            return true;
        }

        // --- SECTOR ---
        if (upper == "SECTOR")
        {
            var sector = world.GetSector(pos);
            value = sector?.Number.ToString() ?? "0";
            return true;
        }

        // --- TERRAIN ---
        var mapData = world.MapData;
        if (mapData == null) return false;

        if (upper == "TERRAIN")
        {
            var cell = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
            value = $"0{cell.TileId:X}";
            return true;
        }
        if (upper == "TERRAIN.Z")
        {
            var cell = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
            value = cell.Z.ToString();
            return true;
        }

        // --- STATICS ---
        if (upper == "STATICS")
        {
            var statics = mapData.GetStatics(pos.Map, pos.X, pos.Y);
            value = statics.Length.ToString();
            return true;
        }
        if (upper.StartsWith("STATICS."))
        {
            // STATICS.n.ID, STATICS.n.COLOR, STATICS.n.Z
            var rest = upper["STATICS.".Length..];
            int dot = rest.IndexOf('.');
            string indexStr = dot >= 0 ? rest[..dot] : rest;
            string sub = dot >= 0 ? rest[(dot + 1)..] : "";

            if (int.TryParse(indexStr, out int idx))
            {
                var statics = mapData.GetStatics(pos.Map, pos.X, pos.Y);
                if (idx < 0 || idx >= statics.Length)
                {
                    value = "0";
                    return true;
                }
                var s = statics[idx];
                value = sub switch
                {
                    "ID" => $"0{s.TileId:X}",
                    "COLOR" => s.Hue.ToString(),
                    "Z" => s.Z.ToString(),
                    _ => "0"
                };
                return true;
            }
        }

        // --- ISNEARTYPE ---
        if (upper.StartsWith("ISNEARTYPE"))
        {
            // Format: ISNEARTYPE type,distance  or  ISNEARTYPE(type,distance)
            var argStr = upper["ISNEARTYPE".Length..].Trim('(', ')', ' ');
            var parts = argStr.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                string typeName = parts[0];
                int dist = parts.Length >= 2 && int.TryParse(parts[1], out int d) ? d : 0;

                // Resolve type name to TileFlag
                TileFlag flagToMatch = typeName switch
                {
                    "T_WATER" or "WATER" => TileFlag.Wet,
                    "T_WALL" or "WALL" => TileFlag.Wall,
                    "T_DOOR" or "DOOR" => TileFlag.Door,
                    "T_ROOF" or "ROOF" => TileFlag.Roof,
                    "T_FOLIAGE" or "FOLIAGE" => TileFlag.Foliage,
                    "T_BRIDGE" or "BRIDGE" => TileFlag.Bridge,
                    "T_CONTAINER" or "CONTAINER" => TileFlag.Container,
                    "T_WEAPON" or "WEAPON" => TileFlag.Weapon,
                    "T_ARMOR" or "ARMOR" => TileFlag.Armor,
                    "T_WEARABLE" or "WEARABLE" => TileFlag.Wearable,
                    "T_LIGHTSOURCE" or "LIGHTSOURCE" => TileFlag.LightSource,
                    "T_WINDOW" or "WINDOW" => TileFlag.Window,
                    "T_IMPASSABLE" or "IMPASSABLE" => TileFlag.Impassable,
                    "T_SURFACE" or "SURFACE" => TileFlag.Surface,
                    "T_DAMAGING" or "DAMAGING" => TileFlag.Damaging,
                    _ => TileFlag.None
                };

                bool found = false;
                if (flagToMatch != TileFlag.None)
                {
                    for (int dx = -dist; dx <= dist && !found; dx++)
                    {
                        for (int dy = -dist; dy <= dist && !found; dy++)
                        {
                            int cx = pos.X + dx;
                            int cy = pos.Y + dy;

                            // Check land tile
                            var terrain = mapData.GetTerrainTile(pos.Map, cx, cy);
                            var landData = mapData.GetLandTileData(terrain.TileId);
                            if ((landData.Flags & flagToMatch) != 0)
                            {
                                found = true;
                                break;
                            }

                            // Check static tiles
                            var statics = mapData.GetStatics(pos.Map, cx, cy);
                            foreach (var s in statics)
                            {
                                var itemData = mapData.GetItemTileData(s.TileId);
                                if ((itemData.Flags & flagToMatch) != 0)
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                value = found ? "1" : "0";
                return true;
            }
        }

        return false;
    }

    private static readonly Random _hueRng = new();

    /// <summary>Parse Sphere random range "{min max}" and return random value.</summary>
    private static ushort ResolveRandomHueRange(string rangeText)
    {
        var span = rangeText.AsSpan().Trim();
        if (span.Length > 2 && span[0] == '{')
        {
            span = span[1..];
            int close = span.IndexOf('}');
            if (close >= 0) span = span[..close];
        }

        var parts = span.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            ushort min = ParseHueValue(parts[0]);
            ushort max = ParseHueValue(parts[1]);
            if (max < min) (min, max) = (max, min);
            return (ushort)_hueRng.Next(min, max + 1);
        }

        return parts.Length == 1 ? ParseHueValue(parts[0]) : (ushort)0;
    }

    private static ushort ParseHueValue(string v)
    {
        v = v.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(v.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort h))
            return h;
        if (v.StartsWith('0') && v.Length > 1 &&
            ushort.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out ushort lh))
            return lh;
        ushort.TryParse(v, out ushort r);
        return r;
    }

    private static bool SetEventsList(List<ResourceId> list, string value)
    {
        list.Clear();
        var parts = value.Split([',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var rid = ParseEventResourceId(part);
            if (rid.IsValid && !list.Contains(rid))
                list.Add(rid);
        }
        return true;
    }

    private static bool ApplyEventsCommand(List<ResourceId> list, string args)
    {
        string text = args.Trim();
        if (string.IsNullOrEmpty(text))
            return true;

        char op = text[0];
        if (op is '+' or '-')
        {
            string name = text[1..].Trim();
            if (name.Length == 0)
                return true;

            var rid = ParseEventResourceId(name);
            if (!rid.IsValid)
                return true;

            if (op == '+')
            {
                if (!list.Contains(rid))
                    list.Add(rid);
            }
            else
            {
                list.Remove(rid);
            }
            return true;
        }

        return SetEventsList(list, text);
    }

    private static ResourceId ParseEventResourceId(string token)
    {
        string text = token.Trim();
        if (text.Length == 0)
            return ResourceId.Invalid;

        // Save/load fallback format: "Events:12345" (ResourceId.ToString()).
        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            string typePart = text[..colon].Trim();
            string indexPart = text[(colon + 1)..].Trim();
            if (typePart.Equals("EVENTS", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(indexPart, out int idx))
            {
                return new ResourceId(ResType.Events, idx);
            }
        }

        // Numeric fallback from custom save scripts.
        if (int.TryParse(text, out int numeric))
            return new ResourceId(ResType.Events, numeric);

        // Primary format used by scripts (e.g. e_staff).
        var rid = ResourceId.FromEventName(text);
        if (rid.IsValid)
            return rid;

        return ResourceId.Invalid;
    }

    public static uint ParseHexOrDecUInt(string val)
    {
        var s = val.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, null, out uint h)) return h;
        }
        else if (s.Length > 1 && s[0] == '0' && !s.All(char.IsDigit))
        {
            if (uint.TryParse(s, NumberStyles.HexNumber, null, out uint h)) return h;
        }
        if (uint.TryParse(s, out uint d)) return d;
        return 0;
    }
}
