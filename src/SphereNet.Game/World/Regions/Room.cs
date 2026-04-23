using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;

namespace SphereNet.Game.World.Regions;

/// <summary>
/// Room definition — sub-region inside a Region (building rooms, boss rooms, etc.).
/// Maps to ROOMDEF in Sphere scripts.
/// </summary>
public class Room : IScriptObj
{
    private static uint _nextUid = 1;

    private string _name = "";
    private ResourceId _resourceId;
    private byte _mapIndex;
    private readonly List<RegionRect> _rects = [];
    private readonly Dictionary<string, string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceId> _events = [];
    private readonly uint _uid;

    public Room()
    {
        _uid = _nextUid++;
    }

    public string Name { get => _name; set => _name = value; }
    public ResourceId ResourceId { get => _resourceId; set => _resourceId = value; }
    public byte MapIndex { get => _mapIndex; set => _mapIndex = value; }
    public IReadOnlyList<RegionRect> Rects => _rects;
    public IReadOnlyList<ResourceId> Events => _events;
    public uint Uid => _uid;

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

    // --- IScriptObj ---

    public string GetName() => _name;

    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "UID": value = _uid.ToString(); return true;
            case "NAME": value = _name; return true;
            case "MAP": value = _mapIndex.ToString(); return true;
            case "RECT": value = _rects.Count.ToString(); return true;
            case "CLIENTS": value = (Region.ClientCountProvider?.Invoke(this) ?? 0).ToString(); return true;
            case "TAGCOUNT": value = _tags.Count.ToString(); return true;
            case "EVENTS":
                value = string.Join(",", _events.Select(e => e.ToString()));
                return true;
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
                // stub — would message all clients in this room
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
