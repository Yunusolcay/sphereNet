using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
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
    private readonly List<TimerFEntry> _timerFEntries = [];

    public sealed record TimerFEntry(long DueTickMs, string FunctionName, string Args);

    /// <summary>Resolve the GameWorld instance (set at startup).</summary>
    public static Func<World.GameWorld>? ResolveWorld;

    /// <summary>Built AOS tooltip (OPL) cached on the object, shared by every
    /// observing client — Source-X CObjBase::SetPropertyList. Expiry is purely
    /// time-based (sphere.ini ToolTipCache seconds, checked on read); the entry
    /// is NOT dropped when the object leaves a client's view, so re-entry and
    /// login bursts reuse the built list. Dies with the object.</summary>
    public Clients.TooltipCacheEntry? TooltipCache { get; set; }

    /// <summary>Script TRIGGER verb (Source-X CV_TRIGGER): the host wires this
    /// to TriggerDispatcher.Fire*TriggerByName so scripts can fire arbitrary
    /// named triggers on an object through the normal handler chain.</summary>
    public static Action<ObjBase, string, ITextConsole>? OnScriptTrigger;

    /// <summary>Source-X CLICK verb bridge. The host resolves the invoking
    /// console to its live client and replays the normal single-click path,
    /// including @Click/@AfterClick and the overhead label packet.</summary>
    public static Func<ObjBase, ITextConsole, bool>? OnScriptSingleClick;

    /// <summary>Broadcast a packet to nearby clients. Wired by the server host.</summary>
    public static Action<Point3D, int, PacketWriter, uint>? BroadcastNearby;

    // --- Dirty tracking for delta-based view updates ---
    private DirtyFlag _dirty;
    private Action<ObjBase>? _dirtyNotify;

    public DirtyFlag DirtyFlags => _dirty;
    public DirtyFlag LastConsumedDirtyFlags { get; private set; }
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
        LastConsumedDirtyFlags = d;
        return d;
    }

    /// <summary>Set the callback invoked on clean→dirty transition (used by GameWorld).</summary>
    public void SetDirtyNotify(Action<ObjBase>? notify) => _dirtyNotify = notify;

    protected bool EmitScriptSound(string args, int range = 18)
    {
        var parts = SplitScriptArgs(args);
        if (parts.Length == 0 || !TryParseScriptUShort(parts[0], out ushort soundId))
            return true;

        byte mode = parts.Length > 1 && TryParseScriptByte(parts[1], out byte parsedMode)
            ? parsedMode
            : (byte)1;

        BroadcastNearby?.Invoke(Position, range, new PacketSound(soundId, X, Y, Z, mode), 0);
        return true;
    }

    protected bool EmitScriptEffect(string args, int range = 18)
    {
        var parts = SplitScriptArgs(args);
        if (parts.Length < 2
            || !TryParseScriptByte(parts[0], out byte effectType)
            || !TryParseScriptUShort(parts[1], out ushort effectId))
            return true;

        byte speed = parts.Length > 2 && TryParseScriptByte(parts[2], out byte parsedSpeed)
            ? parsedSpeed
            : (byte)5;
        byte duration = parts.Length > 3 && TryParseScriptByte(parts[3], out byte parsedDuration)
            ? parsedDuration
            : (byte)1;
        bool explode = parts.Length > 4 && !IsFalseToken(parts[4]);
        uint hue = parts.Length > 5 && TryParseScriptUInt(parts[5], out uint parsedHue) ? parsedHue : 0;
        uint render = parts.Length > 6 && TryParseScriptUInt(parts[6], out uint parsedRender) ? parsedRender : 0;
        ushort particleEffectId = parts.Length > 7 && TryParseScriptUShort(parts[7], out ushort parsedParticleEffectId)
            ? parsedParticleEffectId
            : (ushort)0;
        uint explodeId = parts.Length > 8 && TryParseScriptUInt(parts[8], out uint parsedExplodeId) ? parsedExplodeId : 0;
        ushort explodeSound = parts.Length > 9 && TryParseScriptUShort(parts[9], out ushort parsedExplodeSound)
            ? parsedExplodeSound
            : (ushort)0;
        uint effectUid = parts.Length > 10 && TryParseScriptUInt(parts[10], out uint parsedEffectUid) ? parsedEffectUid : 0;
        byte particleType = parts.Length > 11 && TryParseScriptByte(parts[11], out byte parsedParticleType)
            ? parsedParticleType
            : (byte)0;

        // Source-X writeBasicEffect: oneDirection is false only for EFFECT_BOLT
        // (type 0, a moving projectile) so it rotates to face travel; stationary
        // effect types keep it true.
        bool fixedDir = effectType != 0;

        PacketWriter packet;
        if (particleEffectId != 0 || explodeId != 0)
        {
            packet = new PacketEffectParticle(
                effectType, Uid.Value, Uid.Value, effectId,
                X, Y, Z, X, Y, Z,
                speed, duration, fixedDir, explode,
                hue, render, particleEffectId, explodeId, explodeSound, effectUid, particleType);
        }
        else if (hue != 0 || render != 0)
        {
            packet = new PacketEffectHued(
                effectType, Uid.Value, Uid.Value, effectId,
                X, Y, Z, X, Y, Z,
                speed, duration, fixedDir, explode,
                hue, render);
        }
        else
        {
            packet = new PacketEffect(
                effectType, Uid.Value, Uid.Value, effectId,
                X, Y, Z, X, Y, Z,
                speed, duration, fixedDir, explode);
        }

        BroadcastNearby?.Invoke(Position, range, packet, 0);
        return true;
    }

    /// <summary>Source-X OV_EFFECTLOCATION: same payload family as EFFECT but
    /// aimed at a map location — "x, y, z, motion, id [, speed, loop, explode,
    /// hue, render, effectid, explodeid, explodesound, effectuid, type]".
    /// A motion of -1 with a source char means "bolt from the char to here";
    /// we normalize that to the moving-bolt effect type (0).</summary>
    protected bool EmitScriptEffectLocation(string args, int range = 18)
    {
        var parts = SplitScriptArgs(args);
        if (parts.Length < 5)
            return true;

        static long Num(string s) =>
            SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(s.AsSpan(), out long v) ? v : 0;

        short dx = (short)Num(parts[0]);
        short dy = (short)Num(parts[1]);
        sbyte dz = (sbyte)Num(parts[2]);
        long motionRaw = Num(parts[3]);
        byte effectType = motionRaw < 0 ? (byte)0 : (byte)motionRaw; // -1 → EFFECT_BOLT
        ushort effectId = (ushort)Num(parts[4]);
        if (effectId == 0)
            return true;

        byte speed = parts.Length > 5 ? (byte)Num(parts[5]) : (byte)5;
        byte duration = parts.Length > 6 ? (byte)Num(parts[6]) : (byte)1;
        bool explode = parts.Length > 7 && !IsFalseToken(parts[7]);
        uint hue = parts.Length > 8 ? (uint)Num(parts[8]) : 0;
        uint render = parts.Length > 9 ? (uint)Num(parts[9]) : 0;
        ushort particleEffectId = parts.Length > 10 ? (ushort)Num(parts[10]) : (ushort)0;
        uint explodeId = parts.Length > 11 ? (uint)Num(parts[11]) : 0;
        ushort explodeSound = parts.Length > 12 ? (ushort)Num(parts[12]) : (ushort)0;
        uint effectUid = parts.Length > 13 ? (uint)Num(parts[13]) : 0;
        byte particleType = parts.Length > 14 ? (byte)Num(parts[14]) : (byte)0;

        bool fixedDir = effectType != 0;

        PacketWriter packet;
        if (particleEffectId != 0 || explodeId != 0)
        {
            packet = new PacketEffectParticle(
                effectType, Uid.Value, 0, effectId,
                X, Y, Z, dx, dy, dz,
                speed, duration, fixedDir, explode,
                hue, render, particleEffectId, explodeId, explodeSound, effectUid, particleType);
        }
        else if (hue != 0 || render != 0)
        {
            packet = new PacketEffectHued(
                effectType, Uid.Value, 0, effectId,
                X, Y, Z, dx, dy, dz,
                speed, duration, fixedDir, explode,
                hue, render);
        }
        else
        {
            packet = new PacketEffect(
                effectType, Uid.Value, 0, effectId,
                X, Y, Z, dx, dy, dz,
                speed, duration, fixedDir, explode);
        }

        var target = new Point3D(dx, dy, dz, Position.Map);
        BroadcastNearby?.Invoke(target, range, packet, 0);
        return true;
    }

    protected static string[] SplitScriptArgs(string? args)
        => (args ?? "").Split(
            new[] { ',', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected static bool TryParseScriptUShort(string text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        text = text.Trim();
        if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
            return ushort.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Range check the ADDBUFF/REMOVEBUFF icon argument the way
    /// Source-X CClient.cpp does (BI_START..BI_QTY). Ids outside the enum are
    /// rejected at the verb instead of being sent as a packet the client just
    /// discards. The lower bound is Source-X's BI_START, which is the enum's
    /// zero member, so only the upper bound can actually reject anything.</summary>
    protected static bool IsValidBuffIcon(ushort icon)
        => icon <= (ushort)SphereNet.Core.Enums.BuffIcon.Qty;

    protected static bool TryParseScriptUInt(string text, out uint value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        text = text.Trim();
        if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    protected static bool TryParseScriptByte(string text, out byte value)
    {
        value = 0;
        if (!TryParseScriptUInt(text, out uint parsed) || parsed > byte.MaxValue)
            return false;
        value = (byte)parsed;
        return true;
    }

    private static bool IsFalseToken(string token)
        => token == "0" || token.Equals("false", StringComparison.OrdinalIgnoreCase);

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
    public IReadOnlyList<TimerFEntry> TimerFEntries => _timerFEntries;

    private const int MaxTimersPerObject = 64;

    public void AddTimerF(long delayMs, string functionName, string args)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return;
        if (_timerFEntries.Count >= MaxTimersPerObject)
            return;
        long due = Environment.TickCount64 + Math.Max(0, delayMs);
        _timerFEntries.Add(new TimerFEntry(due, functionName.Trim(), args.Trim()));
        // Register in the world's timer active-set so the per-tick sweep iterates
        // only timer-bearing objects instead of every object. Uses the same world
        // resolver as the rest of ObjBase; a null resolver (no world) simply falls
        // back to the entry still being present should a scan ever run.
        ResolveWorld?.Invoke()?.TrackTimerFObject(this);
    }

    /// <summary>Parse a <c>TIMERF</c> / <c>TIMERFMS</c> argument string —
    /// <c>"&lt;delay&gt;, &lt;funcname&gt; &lt;args&gt;"</c> — and schedule it. The delay
    /// number is multiplied by <paramref name="delayUnitMs"/> (1000 for TIMERF's
    /// seconds, 1 for TIMERFMS's milliseconds). Only the first comma splits the delay
    /// from the payload, so the payload may itself contain commas.</summary>
    internal void ScheduleTimerF(string args, long delayUnitMs)
    {
        int comma = args.IndexOf(',');
        string delayPart = comma >= 0 ? args[..comma] : args;
        string payload = comma >= 0 ? args[(comma + 1)..].Trim() : "";
        if (string.IsNullOrWhiteSpace(payload))
            return;
        int space = payload.IndexOfAny([' ', '\t']);
        string functionName = space >= 0 ? payload[..space].Trim() : payload.Trim();
        string functionArgs = space >= 0 ? payload[(space + 1)..].Trim() : "";
        _ = long.TryParse(delayPart.Trim(), out long delay);
        AddTimerF(delay * delayUnitMs, functionName, functionArgs);
    }

    /// <summary>Restore a persisted TIMERF entry ("remainingMs|functionName|args",
    /// written by the world saver) by re-scheduling it that many ms from now. Returns
    /// true when the line parsed. functionName carries no '|'; args may, so only the
    /// first two delimiters are structural.</summary>
    public bool TryLoadTimerFEntry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split('|', 3);
        if (parts.Length < 2 || !long.TryParse(parts[0], out long remainingMs))
            return false;
        AddTimerF(remainingMs, parts[1], parts.Length > 2 ? parts[2] : "");
        return true;
    }

    public List<TimerFEntry> DequeueDueTimerF(long nowMs)
    {
        var due = new List<TimerFEntry>();
        for (int i = _timerFEntries.Count - 1; i >= 0; i--)
        {
            if (_timerFEntries[i].DueTickMs > nowMs)
                continue;
            due.Add(_timerFEntries[i]);
            _timerFEntries.RemoveAt(i);
        }
        due.Reverse();
        return due;
    }

    /// <summary>Set a tag value on this object.</summary>
    public virtual void SetTag(string key, string value) => _tags.Set(key, value);

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
            // Reaching this getter means the ref already resolved to a live
            // object, so ISVALID is 1 unless it is a tombstone. ISVALIDE is
            // the pack's pervasive spelling (75 uses) — accepted as an alias
            // (the correct token ISVALID is a genuine Source-X keyword).
            case "ISVALID":
            case "ISVALIDE": value = IsDeleted ? "0" : "1"; return true;
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
            case "SEXTANTP": value = ComputeSextant(GetTopLevelPosition()); return true;
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

        // ISDSPEECH.defname — membership in the char's dynamic SPEECH list.
        if (key.StartsWith("ISDSPEECH.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = key.IndexOf('.');
            string spName = key[(dot + 1)..];
            var checkRid = ResourceId.FromString(spName, Core.Enums.ResType.Speech);
            value = this is Characters.Character dspCh && dspCh.DSpeech.Contains(checkRid) ? "1" : "0";
            return true;
        }

        // DISTANCE.<uid> / DISTANCE.<x>,<y> — Source-X OC_DISTANCE: tile
        // distance from this object's TOP-LEVEL position to another object or
        // map point (a contained item measures from its wearer/container).
        if (key.StartsWith("DISTANCE.", StringComparison.OrdinalIgnoreCase))
        {
            string arg = key[9..].Trim();
            var myPos = GetTopLevelPosition();
            if (arg.Contains(','))
            {
                var p = arg.Split(',');
                if (p.Length >= 2 && short.TryParse(p[0], out short px) && short.TryParse(p[1], out short py))
                {
                    value = myPos.GetDistanceTo(new Point3D(px, py, myPos.Z, myPos.Map)).ToString();
                    return true;
                }
            }
            else if (arg.Length > 0)
            {
                string hex = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? arg[2..] : arg;
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint distUid))
                {
                    var other = ResolveWorld?.Invoke()?.FindObject(new Serial(distUid));
                    if (other != null)
                    {
                        value = myPos.GetDistanceTo(other.GetTopLevelPosition()).ToString();
                        return true;
                    }
                }
            }
            return false;
        }

        return false;
    }

    /// <summary>Top-level map position: a character's own position, or — for a
    /// contained/equipped item — the position of the outermost container or
    /// wearer (Source-X GetTopLevelObj()->GetTopPoint()).</summary>
    public Point3D GetTopLevelPosition()
    {
        var world = ResolveWorld?.Invoke();
        ObjBase cur = this;
        for (int depth = 0; depth < 64 && world != null; depth++)
        {
            if (cur is not Items.Item ci || !ci.ContainedIn.IsValid) break;
            var parent = world.FindObject(ci.ContainedIn);
            if (parent == null) break;
            cur = parent;
        }
        return cur.Position;
    }

    /// <summary>Sextant coordinate string for a map point (Source-X
    /// CServerConfig::Calc_MaptoSextant). Zero point 1323,1624; longitude uses
    /// the real world width (5120) on maps 0/1, else the map's own width.</summary>
    private static string ComputeSextant(Point3D pos)
    {
        const int zeroX = 1323, zeroY = 1624, uoSizeXReal = 0x1400; // 5120
        var world = ResolveWorld?.Invoke();
        var (mapW, mapH) = world?.MapData?.GetMapSize(pos.Map) ?? (uoSizeXReal, 4096);
        if (mapH <= 0) mapH = 4096;
        int iLat = (pos.Y - zeroY) * 360 * 60 / mapH;
        int longDiv = pos.Map <= 1 ? uoSizeXReal : (mapW > 0 ? mapW : uoSizeXReal);
        int iLong = (pos.X - zeroX) * 360 * 60 / longDiv;
        return $"{Math.Abs(iLat / 60)}o {Math.Abs(iLat % 60)}'{(iLat <= 0 ? "N" : "S")}, " +
               $"{Math.Abs(iLong / 60)}o {Math.Abs(iLong % 60)}'{(iLong >= 0 ? "E" : "W")}";
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

        // SFX is the legacy Sphere alias of SOUND (55i-era scripts use both).
        if (trimmedKey.Equals("SOUND", StringComparison.OrdinalIgnoreCase) ||
            trimmedKey.Equals("SFX", StringComparison.OrdinalIgnoreCase))
            return EmitScriptSound(args);
        if (trimmedKey.Equals("EFFECT", StringComparison.OrdinalIgnoreCase))
            return EmitScriptEffect(args);

        switch (key.ToUpperInvariant())
        {
            case "DAMAGE":
            {
                string[] parts = SplitScriptArgs(args);
                if (parts.Length == 0 ||
                    !SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[0].AsSpan(), out long rawDamage) ||
                    rawDamage <= 0)
                    return false;

                var damageType = Combat.DamageType.HitBlunt;
                if (parts.Length > 1 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[1].AsSpan(), out long rawType))
                    damageType = (Combat.DamageType)unchecked((ushort)rawType);

                Characters.Character? damageSource = null;
                if (parts.Length > 2 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[2].AsSpan(), out long rawSource) &&
                    rawSource > 0 && rawSource <= uint.MaxValue)
                    damageSource = ResolveWorld?.Invoke()?.FindChar(new Serial((uint)rawSource));

                static int Percent(string[] values, int index) =>
                    index < values.Length &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(values[index].AsSpan(), out long value)
                        ? (int)Math.Clamp(value, int.MinValue, int.MaxValue)
                        : 0;

                Combat.CombatEngine.ApplyScriptDamage(
                    this, (int)Math.Clamp(rawDamage, 0, short.MaxValue), damageType, damageSource,
                    Percent(parts, 3), Percent(parts, 4), Percent(parts, 5),
                    Percent(parts, 6), Percent(parts, 7));
                return true;
            }
            // Source-X TIMERF schedules a delayed function/verb on this object: the
            // delay is in SECONDS, while TIMERFMS takes MILLISECONDS (same payload).
            case "TIMERF":
                ScheduleTimerF(args, delayUnitMs: 1000L);
                return true;
            case "TIMERFMS":
                ScheduleTimerF(args, delayUnitMs: 1L);
                return true;
            case "SHOW":
                source.SysMessage($"{GetName()} [0x{Uid.Value:X8}] P={Position.X},{Position.Y},{Position.Z},{Position.Map}");
                return true;
            case "SAY":
            case "EMOTE":
            case "SYSMESSAGE":
            case "SYSMESSAGEF":
            // Source-X SMSG family — localized-message aliases of SYSMESSAGE
            // (CChar.cpp CHV_SMSG*). Scripts using them were silent no-ops.
            case "SMSG":
            case "SMSGL":
            case "SMSGLEX":
            case "SMSGU":
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
            case "DSPEECH":
            {
                // Per-char dynamic SPEECH list (Source-X m_Speech). Append/remove
                // with +/-<resource>; a bare list replaces it.
                if (this is Characters.Character dch)
                    return ApplySpeechListCommand(dch.DSpeech, args);
                return true;
            }
            case "TRIGGER":
            {
                // Source-X CV_TRIGGER: fire @<name> on this object through the
                // normal handler chain (EVENTS/TEVENTS/ITEMDEF/typedef).
                string trigName = args.Trim().TrimStart('@');
                if (trigName.Length > 0)
                    OnScriptTrigger?.Invoke(this, trigName, source);
                return true;
            }
            case "REMOVE":
            // Source-X CHV_DESTROY: hard removal that bypasses the @Destroy
            // veto — same engine path as REMOVE in SphereNet.
            case "DESTROY":
                if (this is Items.Item delItem) delItem.RemoveFromWorld();
                else if (this is Characters.Character delCh)
                {
                    var destroyWorld = ResolveWorld?.Invoke();
                    if (destroyWorld != null) destroyWorld.DeleteObject(delCh);
                    else delCh.Delete();
                }
                return true;
            case "TIMER":
                if (long.TryParse(args, out long timerVal))
                {
                    // Sphere: a negative TIMER DISABLES the timer (scripts
                    // toggle loops off with TIMER -1); clamping to 0 made it
                    // fire immediately instead — the flash-robe off switch
                    // restarted the color loop.
                    SetTimeout(timerVal < 0
                        ? 0
                        : Environment.TickCount64 + timerVal * 1000);
                }
                return true;
            case "FIX":
            {
                // Source-X CObjBase fix: re-seat on the standing surface. A
                // CHARACTER goes through the shared standing resolver — the
                // terrain-only GetEffectiveZ dropped it under house floors,
                // ship decks and dungeon statics. Items keep the plain
                // terrain drop.
                var fixWorld = ResolveWorld?.Invoke();
                if (fixWorld?.MapData != null)
                {
                    var p = Position;
                    if (this is Characters.Character fixCh)
                    {
                        var fixStand = fixWorld.Standing.ResolveStandingSurface(
                            fixCh, p.Map, p.X, p.Y, p.Z,
                            Movement.WalkCheck.StandingPolicy.Settle);
                        sbyte fixZ = fixStand.Found
                            ? fixStand.Z
                            : fixWorld.MapData.GetEffectiveZ(p.Map, p.X, p.Y);
                        fixWorld.MoveCharacter(fixCh, new Point3D(p.X, p.Y, fixZ, p.Map));
                    }
                    else if (this is Items.Item fixItem && !fixItem.ContainedIn.IsValid)
                    {
                        sbyte fixZ = fixWorld.MapData.GetEffectiveZ(p.Map, p.X, p.Y);
                        Position = new Point3D(p.X, p.Y, fixZ, p.Map);
                    }
                }
                return true;
            }
            case "SOUND":
                // Sound effect — handled by the callback if set
                if (ushort.TryParse(args, out ushort _))
                    return true; // Sound playback wired at GameClient level
                return true;
            case "EFFECT":
                // Visual effect — handled by the callback if set
                return true;
            case "EFFECTLOCATION":
                return EmitScriptEffectLocation(args);
            // Source-X OV_GOAWAKE/OV_GOSLEEP — flip the object's tick-sleep
            // state (a sleeping object is skipped by the sector tick loops).
            case "GOAWAKE":
                GoAwake();
                return true;
            case "GOSLEEP":
                GoSleep();
                return true;
            case "MOVETO":
            case "P":
            {
                if (!TryParseScriptPoint(args, Position, out Point3D destination))
                    return false;
                return TryMoveScriptObject(destination);
            }
            case "MOVENEAR":
            {
                string[] parts = SplitScriptArgs(args);
                if (parts.Length == 0 ||
                    !SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[0].AsSpan(), out long uidValue) ||
                    uidValue <= 0 || uidValue > uint.MaxValue)
                    return false;

                var near = ResolveWorld?.Invoke()?.FindObject(new Serial((uint)uidValue));
                if (near == null || near.IsDeleted)
                    return false;

                int distance = 1;
                if (parts.Length > 1 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[1].AsSpan(), out long parsedDistance))
                    distance = (int)Math.Clamp(parsedDistance, 1L, short.MaxValue);

                Point3D centre = near.GetTopLevelPosition();
                int x = Math.Clamp(centre.X + distance, short.MinValue, short.MaxValue);
                return TryMoveScriptObject(new Point3D((short)x, centre.Y, centre.Z, centre.Map));
            }
            case "CLICK":
            {
                ObjBase clickTarget = this;
                if (!string.IsNullOrWhiteSpace(args))
                {
                    if (!IsChar ||
                        !SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(args.AsSpan(), out long clickUid) ||
                        clickUid <= 0 || clickUid > uint.MaxValue)
                        return false;
                    clickTarget = ResolveWorld?.Invoke()?.FindObject(new Serial((uint)clickUid)) ?? this;
                }
                return OnScriptSingleClick?.Invoke(clickTarget, source) == true;
            }
            case "USEITEM":
            {
                ObjBase useTarget = this;
                if (!string.IsNullOrWhiteSpace(args))
                {
                    if (!IsChar ||
                        !SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(args.AsSpan(), out long useUid) ||
                        useUid <= 0 || useUid > uint.MaxValue)
                        return false;
                    useTarget = ResolveWorld?.Invoke()?.FindObject(new Serial((uint)useUid)) ?? this;
                }

                if (useTarget.TryExecuteCommand("USE", "", source))
                    return true;
                return useTarget.TryExecuteCommand("DCLICK", "", source);
            }
            case "SAYUA":
            {
                // Source-X OV_SAYUA: "Color, Mode, Font, Lang, Text" — exactly
                // five comma fields; the text field keeps any further commas.
                var f = args.Split(',', 5);
                if (f.Length < 5)
                    return true;
                ushort hue = f[0].Trim().Length > 0 && TryParseScriptUShort(f[0].Trim(), out ushort h) ? h : (ushort)0x03B2;
                byte mode = f[1].Trim().Length > 0 && TryParseScriptByte(f[1].Trim(), out byte m) ? m : (byte)0;
                byte font = f[2].Trim().Length > 0 && TryParseScriptByte(f[2].Trim(), out byte fo) ? fo : (byte)3;
                string lang = f[3].Trim().Length > 0 ? f[3].Trim() : "ENU";
                string text = f[4].Trim();
                if (text.Length == 0)
                    return true;
                ushort body = this is Characters.Character sayChar ? sayChar.BodyId : (ushort)0;
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketSpeechUnicodeOut(
                        Uid.Value, body, mode, hue, font, lang, GetName(), text), 0);
                return true;
            }
            // Source-X OV_PROPLIST/OV_BASEPROPLIST/OV_BASETAGLIST diagnostic
            // dumps — output lands on the invoking console, or the server log
            // when the argument is the literal "log".
            case "PROPLIST":
            {
                var sink = ResolveDumpSink(args, source);
                int propCount = 0;
                foreach (string propKey in EnumeratePropListKeys())
                {
                    if (TryGetProperty(propKey, out string propVal) && propVal.Length > 0)
                    {
                        sink($"{propKey}={propVal}");
                        propCount++;
                    }
                }
                sink($"[PROPLIST] {propCount} properties");
                return true;
            }
            case "BASEPROPLIST":
            {
                var sink = ResolveDumpSink(args, source);
                DumpBaseProperties(line => sink("[Base]" + line));
                return true;
            }
            case "BASETAGLIST":
            {
                var sink = ResolveDumpSink(args, source);
                DumpBaseTags(line => sink("[Base]TAG." + line));
                return true;
            }
        }
        return false;
    }

    private bool TryMoveScriptObject(Point3D destination)
    {
        var world = ResolveWorld?.Invoke();
        return this switch
        {
            Characters.Character ch => world?.MoveCharacter(ch, destination) ?? MoveWithoutWorld(destination),
            Items.Item item => world?.PlaceItem(item, destination) ?? MoveWithoutWorld(destination),
            _ => false
        };
    }

    private bool MoveWithoutWorld(Point3D destination)
    {
        Position = destination;
        return true;
    }

    private static bool TryParseScriptPoint(string text, Point3D current, out Point3D point)
    {
        point = current;
        string[] parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            !short.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out short x) ||
            !short.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out short y))
            return false;

        sbyte z = current.Z;
        byte map = current.Map;
        if (parts.Length > 2 && parts[2].Length > 0 &&
            !sbyte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out z))
            return false;
        if (parts.Length > 3 && parts[3].Length > 0 &&
            !byte.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out map))
            return false;
        point = new Point3D(x, y, z, map);
        return true;
    }

    /// <summary>Server-log sink used by the *LIST diagnostic verbs when the
    /// script passes the literal <c>log</c> argument (Source-X redirects the
    /// dump to the server console). Wired by the host at boot.</summary>
    public static Action<string>? DiagnosticLog;

    private static Action<string> ResolveDumpSink(string args, ITextConsole source) =>
        args.Trim().Equals("log", StringComparison.OrdinalIgnoreCase) && DiagnosticLog != null
            ? DiagnosticLog
            : line => source.SysMessage(line);

    /// <summary>Property keys enumerated by the PROPLIST diagnostic verb.
    /// Overridden by Item/Character with their type-specific surfaces.</summary>
    protected virtual IEnumerable<string> EnumeratePropListKeys() =>
        ["NAME", "COLOR", "P", "TIMER", "LINK"];

    /// <summary>BASEPROPLIST body — dump the creating definition's properties.</summary>
    protected virtual void DumpBaseProperties(Action<string> sink) { }

    /// <summary>BASETAGLIST body — dump the creating definition's TAGs.</summary>
    protected virtual void DumpBaseTags(Action<string> sink) { }

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
                    MarkDirty(DirtyFlag.Hue);
                    if (this is Items.Item changedItem)
                        Items.Item.OnVisualUpdate?.Invoke(changedItem);
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
                _attr = ParseObjAttributes(value);
                return true;
            case "EVENTS":
                if (this is Characters.Character ch)
                    return SetEventsList(ch.Events, value);
                if (this is Items.Item item)
                    return SetEventsList(item.Events, value);
                return true;
            case "DSPEECH":
                if (this is Characters.Character dspCh)
                    return ApplySpeechListCommand(dspCh.DSpeech, value);
                return true;
            case "TIMERMS":
                if (long.TryParse(value, out long timerMs) && timerMs > 0)
                    SetTimeout(Environment.TickCount64 + timerMs);
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
            if (EngineTags.IsEphemeral(tagKey))
                return true;
            SetTag(tagKey, value);
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

    public void SetTimeout(long timeoutMs)
    {
        _timeout = timeoutMs;
        // A timed ITEM off the ground (worn gear, contained items) is in no
        // sector tick list — register it with the world's off-ground timer
        // pump so its @Timer still fires (the flash-robe class of scripts).
        if (timeoutMs > 0 && this is Items.Item timedItem && !timedItem.IsOnGround)
            ResolveWorld?.Invoke()?.TrackOffGroundTimer(timedItem);
    }
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
    /// <summary>Classify a land tile into a t_* terrain type name (Source-X
    /// terrain item-type). Water/rock come from the tile flags; the natural
    /// ground kind is read from the tiledata name, defaulting to t_grass for
    /// plain walkable land.</summary>
    private static string ClassifyTerrainType(MapData.Tiles.LandTileData ld)
    {
        if (ld.IsWet) return "t_water";
        string n = (ld.Name ?? "").ToLowerInvariant();
        if (n.Contains("dirt") || n.Contains("mud") || n.Contains("earth")) return "t_dirt";
        if (n.Contains("sand") || n.Contains("desert") || n.Contains("beach")) return "t_sand";
        if (n.Contains("snow") || n.Contains("ice")) return "t_snow";
        if (n.Contains("jungle")) return "t_jungle";
        if (n.Contains("forest") || n.Contains("tree") || n.Contains("wood")) return "t_forest";
        if (n.Contains("grass")) return "t_grass";
        if (n.Contains("rock") || n.Contains("mountain") || n.Contains("cave")) return "t_rock";
        // Impassable non-named land reads as rock; plain walkable land as grass.
        return ld.IsImpassable ? "t_rock" : "t_grass";
    }

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

        // --- ISNEARTYPE --- (before the mapData guard: the dynamic-item search
        // works without terrain data; only the terrain-flag search needs it).
        if (upper.StartsWith("ISNEARTYPE", StringComparison.Ordinal))
        {
            // Format: ISNEARTYPE type,distance  or  ISNEARTYPE(type,distance)
            var argStr = upper["ISNEARTYPE".Length..].Trim('(', ')', ' ');
            var parts = argStr.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                string typeName = parts[0];
                int dist = parts.Length >= 2 && int.TryParse(parts[1], out int d) ? d : 0;

                // Resolve type name to a terrain TileFlag (else it's an item IT_TYPE).
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
                var terrainData = world.MapData;
                if (flagToMatch != TileFlag.None && terrainData != null)
                {
                    for (int dx = -dist; dx <= dist && !found; dx++)
                    {
                        for (int dy = -dist; dy <= dist && !found; dy++)
                        {
                            int cx = pos.X + dx;
                            int cy = pos.Y + dy;

                            var terrain = terrainData.GetTerrainTile(pos.Map, cx, cy);
                            var landData = terrainData.GetLandTileData(terrain.TileId);
                            if ((landData.Flags & flagToMatch) != 0)
                            {
                                found = true;
                                break;
                            }

                            var statics = terrainData.GetStatics(pos.Map, cx, cy);
                            foreach (var s in statics)
                            {
                                var itemData = terrainData.GetItemTileData(s.TileId);
                                if ((itemData.Flags & flagToMatch) != 0)
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                // A non-terrain name (e.g. T_MOONGATE) is a dynamic-item IT_TYPE:
                // Source-X ISNEARTYPE also scans nearby dynamic items by type.
                if (!found && flagToMatch == TileFlag.None)
                {
                    var itemType = Items.Item.ParseItemType(typeName);
                    if (itemType is not (ItemType.Invalid or ItemType.Normal))
                    {
                        foreach (var it in world.GetItemsInRange(pos, dist))
                        {
                            if (!it.IsDeleted && it.ItemType == itemType)
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                }

                value = found ? "1" : "0";
                return true;
            }
        }

        // --- TERRAIN ---
        var mapData = world.MapData;
        if (mapData == null) return false;

        // P.TYPE — the t_* terrain type at this point (Source-X CPointBase
        // PT_TYPE via GetTerrainItemTypeDef). Source-X reads a TileTypes
        // table (script [TERRAIN] defs), which neither this pack nor the
        // engine ships, so the type is derived from the land tiledata:
        // water/rock from flags, else the natural-ground kind from the name
        // (falling back to t_grass for plain walkable land). Enough for the
        // treasure-map placement whitelist that depends on it.
        if (upper == "P.TYPE")
        {
            var cell = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
            var ld = mapData.GetLandTileData(cell.TileId);
            value = ClassifyTerrainType(ld);
            return true;
        }

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
        if (upper.StartsWith("STATICS.", StringComparison.Ordinal))
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
        bool isMultiValue = value.Contains(',') || value.Contains(' ');
        if (isMultiValue)
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

    private static bool ApplySpeechListCommand(List<ResourceId> list, string args)
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

            var rid = ResourceId.FromString(name, ResType.Speech);
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

        // A multi-value assignment replaces the list; a single bare name appends
        // (so successive saved DSPEECH lines accumulate, matching EVENTS).
        bool isMultiValue = text.Contains(',') || text.Contains(' ');
        if (isMultiValue)
            list.Clear();
        var parts = text.Split([',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var rid = ResourceId.FromString(part, ResType.Speech);
            if (rid.IsValid && !list.Contains(rid))
                list.Add(rid);
        }
        return true;
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

        // Fallback: typedef names (e.g. t_custom_spawner_char) are registered as
        // ResType.TypeDef, not ResType.Events. ResolveDefName covers both.
        var resolved = DefinitionLoader.StaticResources?.ResolveDefName(text) ?? ResourceId.Invalid;
        if (resolved.IsValid && resolved.Type is ResType.TypeDef or ResType.Events)
            return resolved;

        return ResourceId.Invalid;
    }

    public static uint ParseHexOrDecUInt(string val)
    {
        var s = val.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, null, out uint h)) return h;
        }
        else if (s.Length > 1 && s[0] == '0')
        {
            if (uint.TryParse(s, NumberStyles.HexNumber, null, out uint h)) return h;
        }
        if (uint.TryParse(s, out uint d)) return d;
        return 0;
    }

    private static ObjAttributes ParseObjAttributes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ObjAttributes.None;

        ObjAttributes result = ObjAttributes.None;
        var parts = value.Split(['|', ',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            string token = part.Trim();
            if (token.Length == 0)
                continue;

            uint numeric = ParseHexOrDecUInt(token);
            if (numeric != 0 || token == "0")
            {
                result |= (ObjAttributes)numeric;
                continue;
            }

            string enumName = token;
            if (enumName.StartsWith("ATTR_", StringComparison.OrdinalIgnoreCase))
                enumName = enumName[5..];
            enumName = enumName.Replace("MOVE_ALWAYS", "Move_Always", StringComparison.OrdinalIgnoreCase)
                .Replace("MOVE_NEVER", "Move_Never", StringComparison.OrdinalIgnoreCase);

            if (Enum.TryParse(enumName, ignoreCase: true, out ObjAttributes attr))
                result |= attr;
        }

        return result;
    }
}
