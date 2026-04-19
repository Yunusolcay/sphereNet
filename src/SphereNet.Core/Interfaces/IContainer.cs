using SphereNet.Core.Types;

namespace SphereNet.Core.Interfaces;

/// <summary>
/// Container interface. Maps to CContainer in Source-X.
/// Manages a list of items with weight tracking.
/// </summary>
public interface IContainer
{
    int ItemCount { get; }
    int TotalWeight { get; }
    void AddItem(IScriptObj item);
    void RemoveItem(IScriptObj item);
    IScriptObj? FindItem(Serial uid);
    IEnumerable<IScriptObj> GetItems();
}
