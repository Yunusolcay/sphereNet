namespace SphereNet.Scripting.Variables;

/// <summary>
/// Named list storage. Maps to CListDefMap in Source-X.
/// Supports LIST.* operations (add, remove, find, count).
/// </summary>
public sealed class ListMap
{
    private readonly Dictionary<string, List<string>> _lists = new(StringComparer.OrdinalIgnoreCase);

    public List<string> GetOrCreate(string name)
    {
        if (!_lists.TryGetValue(name, out var list))
        {
            list = [];
            _lists[name] = list;
        }
        return list;
    }

    public int GetCount(string name)
    {
        return _lists.TryGetValue(name, out var list) ? list.Count : 0;
    }

    public void Add(string name, string value)
    {
        GetOrCreate(name).Add(value);
    }

    public bool Remove(string name, string value)
    {
        if (_lists.TryGetValue(name, out var list))
            return list.Remove(value);
        return false;
    }

    public string? GetAt(string name, int index)
    {
        if (_lists.TryGetValue(name, out var list) && index >= 0 && index < list.Count)
            return list[index];
        return null;
    }

    public void Clear(string name)
    {
        if (_lists.TryGetValue(name, out var list))
            list.Clear();
    }

    public void ClearAll() => _lists.Clear();

    public bool Has(string name) => _lists.ContainsKey(name) && _lists[name].Count > 0;

    public int FindIndex(string name, string value)
    {
        if (_lists.TryGetValue(name, out var list))
            return list.FindIndex(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));
        return -1;
    }
}
