using SphereNet.Core.Types;

namespace SphereNet.Core.Collections;

/// <summary>
/// Resource hash table. Maps to CResourceHash in Source-X.
/// Stores CResourceDef entries indexed by ResourceId.
/// </summary>
public sealed class SortedResourceHash<T> where T : class
{
    private readonly SortedDictionary<uint, T> _entries = [];

    public int Count => _entries.Count;

    public void Add(ResourceId rid, T entry)
    {
        uint key = Pack(rid);
        _entries[key] = entry;
    }

    public T? Get(ResourceId rid)
    {
        uint key = Pack(rid);
        return _entries.GetValueOrDefault(key);
    }

    public bool TryGet(ResourceId rid, out T? entry)
    {
        uint key = Pack(rid);
        return _entries.TryGetValue(key, out entry);
    }

    public bool Remove(ResourceId rid)
    {
        return _entries.Remove(Pack(rid));
    }

    public bool Contains(ResourceId rid)
    {
        return _entries.ContainsKey(Pack(rid));
    }

    public void Replace(ResourceId rid, T newEntry)
    {
        _entries[Pack(rid)] = newEntry;
    }

    public IEnumerable<T> GetAll() => _entries.Values;

    public void Clear() => _entries.Clear();

    private static uint Pack(ResourceId rid) =>
        ((uint)rid.Type << 24) | ((uint)rid.Index & 0x00FFFFFF);
}
