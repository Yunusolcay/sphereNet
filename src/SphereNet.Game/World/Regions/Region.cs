using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;

namespace SphereNet.Game.World.Regions;

/// <summary>
/// Named world region with flags and triggers. Maps to CRegion in Source-X.
/// Regions define areas with special properties (guarded, no-pvp, dungeons, etc.).
/// </summary>
public class Region : IScriptObj
{
    private static uint _nextUid = 1;

    private string _name = "";
    private ResourceId _resourceId;
    private RegionFlag _flags;
    private readonly List<RegionRect> _rects = [];
    private string? _group;
    private byte _mapIndex;
    private readonly Dictionary<string, string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceId> _events = [];
    private readonly List<ResourceId> _regionTypes = [];
    private readonly uint _uid;
    private string? _defName;
    private Point3D? _p;

    public static Func<IScriptObj, int>? ClientCountProvider { get; set; }

    public Region()
    {
        _uid = _nextUid++;
    }

    public string Name { get => _name; set => _name = value; }
    public ResourceId ResourceId { get => _resourceId; set => _resourceId = value; }
    public RegionFlag Flags { get => _flags; set => _flags = value; }
    public byte MapIndex { get => _mapIndex; set => _mapIndex = value; }
    public string? Group { get => _group; set => _group = value; }
    public IReadOnlyList<RegionRect> Rects => _rects;
    public IReadOnlyList<ResourceId> Events => _events;
    public IReadOnlyList<ResourceId> RegionTypes => _regionTypes;
    public uint Uid => _uid;
    public string? DefName { get => _defName; set => _defName = value; }
    public Point3D? P { get => _p; set => _p = value; }

    public bool IsFlag(RegionFlag flag) => (_flags & flag) != 0;
    public void SetTag(string key, string value) => _tags[key.ToUpperInvariant()] = value;
    public bool TryGetTag(string key, out string? value) => _tags.TryGetValue(key.ToUpperInvariant(), out value);
    public bool RemoveTag(string key) => _tags.Remove(key.ToUpperInvariant());

    public int ClearTags(string? prefix = null)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            int count = _tags.Count;
            _tags.Clear();
            return count;
        }
        var toRemove = _tags.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in toRemove)
            _tags.Remove(k);
        return toRemove.Count;
    }

    public void AddEvent(ResourceId rid) => _events.Add(rid);
    public void RemoveEvent(ResourceId rid) => _events.Remove(rid);

    public void AddRegionType(ResourceId rid) => _regionTypes.Add(rid);
    public void RemoveRegionType(ResourceId rid) => _regionTypes.Remove(rid);

    public bool IsGuarded => IsFlag(RegionFlag.Guarded);
    public bool NoMagic => IsFlag(RegionFlag.NoMagic);
    public bool NoPvP => IsFlag(RegionFlag.NoPvP);

    public void AddRect(short x1, short y1, short x2, short y2)
    {
        _rects.Add(new RegionRect(x1, y1, x2, y2));
    }

    public bool Contains(Point3D pt)
    {
        if (pt.Map != _mapIndex) return false;
        foreach (var r in _rects)
        {
            if (r.Contains(pt.X, pt.Y))
                return true;
        }
        return false;
    }

    // --- Flag helpers for TryGet/SetProperty ---

    private bool GetFlagBool(RegionFlag flag) => (_flags & flag) != 0;

    private void SetFlagBool(RegionFlag flag, string val)
    {
        if (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase))
            _flags |= flag;
        else
            _flags &= ~flag;
    }

    // --- IScriptObj ---

    public string GetName() => _name;

    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "UID": value = _uid.ToString(); return true;
            case "DEFNAME": value = _defName ?? ""; return true;
            case "NAME": value = _name; return true;
            case "MAP": value = _mapIndex.ToString(); return true;
            case "FLAGS": value = ((uint)_flags).ToString(); return true;
            case "GROUP": value = _group ?? ""; return true;
            case "RECT": value = _rects.Count.ToString(); return true;
            case "CLIENTS": value = (ClientCountProvider?.Invoke(this) ?? 0).ToString(); return true;
            case "P":
                if (_p.HasValue)
                    value = $"{_p.Value.X},{_p.Value.Y},{_p.Value.Z},{_p.Value.Map}";
                else
                    value = "0,0,0,0";
                return true;
            case "TAGCOUNT": value = _tags.Count.ToString(); return true;
            case "EVENTS":
                value = string.Join(",", _events.Select(e => e.ToString()));
                return true;

            // Flag properties
            case "GUARDED": value = GetFlagBool(RegionFlag.Guarded) ? "1" : "0"; return true;
            case "NOPVP": value = GetFlagBool(RegionFlag.NoPvP) ? "1" : "0"; return true;
            case "NOBUILD": value = GetFlagBool(RegionFlag.NoBuild) ? "1" : "0"; return true;
            case "MAGIC": value = GetFlagBool(RegionFlag.NoMagic) ? "0" : "1"; return true;
            case "NOMAGIC": value = GetFlagBool(RegionFlag.NoMagic) ? "1" : "0"; return true;
            case "RECALLIN": value = GetFlagBool(RegionFlag.Recall) ? "1" : "0"; return true;
            case "MARK": value = GetFlagBool(RegionFlag.Mark) ? "1" : "0"; return true;
            case "SAFE": value = GetFlagBool(RegionFlag.Safe) ? "1" : "0"; return true;
            case "UNDERGROUND": value = GetFlagBool(RegionFlag.Underground) ? "1" : "0"; return true;
        }

        // RESOURCES / RESOURCES.n
        if (upper == "RESOURCES")
        {
            value = _regionTypes.Count.ToString();
            return true;
        }
        if (upper.StartsWith("RESOURCES.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper.AsSpan(10), out int resIdx) && resIdx >= 0 && resIdx < _regionTypes.Count)
            {
                value = _regionTypes[resIdx].ToString();
                return true;
            }
            return false;
        }

        // RECT.n
        if (upper.StartsWith("RECT.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper.AsSpan(5), out int rectIdx) && rectIdx >= 0 && rectIdx < _rects.Count)
            {
                var r = _rects[rectIdx];
                value = $"{r.X1},{r.Y1},{r.X2},{r.Y2}";
                return true;
            }
            return false;
        }

        // TAGAT.n / TAGAT.n.KEY / TAGAT.n.VAL
        if (upper.StartsWith("TAGAT.", StringComparison.Ordinal))
        {
            var rest = upper[6..];
            var tagList = _tags.ToList();
            var parts = rest.Split('.');
            if (int.TryParse(parts[0], out int tagIdx) && tagIdx >= 0 && tagIdx < tagList.Count)
            {
                if (parts.Length == 1)
                {
                    value = $"{tagList[tagIdx].Key}={tagList[tagIdx].Value}";
                    return true;
                }
                if (parts[1] == "KEY") { value = tagList[tagIdx].Key; return true; }
                if (parts[1] == "VAL") { value = tagList[tagIdx].Value; return true; }
            }
            return false;
        }

        // TAG.key
        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
        {
            string tagKey = upper[4..];
            if (_tags.TryGetValue(tagKey, out var tagVal))
            {
                value = tagVal;
                return true;
            }
            value = "";
            return true;
        }

        // ISEVENT.defname
        if (upper.StartsWith("ISEVENT.", StringComparison.Ordinal))
        {
            string evName = key[8..];
            var checkRid = ResourceId.FromString(evName, ResType.Events);
            value = _events.Contains(checkRid) ? "1" : "0";
            return true;
        }

        return false;
    }

    public bool TrySetProperty(string key, string val)
    {
        var upper = key.ToUpperInvariant();

        // Flag properties
        switch (upper)
        {
            case "GUARDED": SetFlagBool(RegionFlag.Guarded, val); return true;
            case "NOPVP": SetFlagBool(RegionFlag.NoPvP, val); return true;
            case "NOBUILD": SetFlagBool(RegionFlag.NoBuild, val); return true;
            case "NOMAGIC": SetFlagBool(RegionFlag.NoMagic, val); return true;
            case "MAGIC":
                // MAGIC=1 means NoMagic OFF, MAGIC=0 means NoMagic ON
                if (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase))
                    _flags &= ~RegionFlag.NoMagic;
                else
                    _flags |= RegionFlag.NoMagic;
                return true;
            case "RECALLIN": SetFlagBool(RegionFlag.Recall, val); return true;
            case "MARK": SetFlagBool(RegionFlag.Mark, val); return true;
            case "SAFE": SetFlagBool(RegionFlag.Safe, val); return true;
            case "UNDERGROUND": SetFlagBool(RegionFlag.Underground, val); return true;
            case "FLAGS":
                if (uint.TryParse(val, out uint flagsVal))
                    _flags = (RegionFlag)flagsVal;
                return true;
        }

        // TAG.key
        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
        {
            string tagKey = upper[4..];
            if (string.IsNullOrEmpty(val))
                _tags.Remove(tagKey);
            else
                _tags[tagKey] = val;
            return true;
        }

        // RESOURCES +/-defname
        if (upper == "RESOURCES")
        {
            if (val.StartsWith('+'))
            {
                var rid = ResourceId.FromString(val[1..], ResType.RegionType);
                if (!_regionTypes.Contains(rid))
                    _regionTypes.Add(rid);
            }
            else if (val.StartsWith('-'))
            {
                var rid = ResourceId.FromString(val[1..], ResType.RegionType);
                _regionTypes.Remove(rid);
            }
            return true;
        }

        // EVENTS +/-defname
        if (upper == "EVENTS")
        {
            if (val.StartsWith('+'))
            {
                var rid = ResourceId.FromString(val[1..], ResType.Events);
                if (!_events.Contains(rid))
                    _events.Add(rid);
            }
            else if (val.StartsWith('-'))
            {
                var rid = ResourceId.FromString(val[1..], ResType.Events);
                _events.Remove(rid);
            }
            return true;
        }

        return false;
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        var upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "ALLCLIENTS":
                // stub — would message all clients in this region
                return true;
            case "CLEARTAGS":
                ClearTags(string.IsNullOrEmpty(args) ? null : args);
                return true;
            case "TAGLIST":
                // stub — would list tags to console
                return true;
        }

        return false;
    }

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
        => TriggerResult.Default;
}

public readonly struct RegionRect
{
    public short X1 { get; }
    public short Y1 { get; }
    public short X2 { get; }
    public short Y2 { get; }

    public RegionRect(short x1, short y1, short x2, short y2)
    {
        X1 = Math.Min(x1, x2);
        Y1 = Math.Min(y1, y2);
        X2 = Math.Max(x1, x2);
        Y2 = Math.Max(y1, y2);
    }

    public bool Contains(short x, short y) =>
        x >= X1 && x <= X2 && y >= Y1 && y <= Y2;
}
