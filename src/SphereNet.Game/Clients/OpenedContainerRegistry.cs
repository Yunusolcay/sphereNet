using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Clients;

/// <summary>
/// Which containers this client has actually been shown, and the shape they had at
/// the time. Source-X <c>CClient::m_openedContainers</c>; the pickup path consults
/// it before letting an item out of a container (CCharAct.cpp:2856-2895).
///
/// Without it, knowing a child's uid was enough to lift an item out of a container
/// the client had never opened - a locked chest included, since nothing in the
/// pickup path looked at the chest at all.
///
/// Each entry records what the container hung off when it was opened, so a chest
/// that has since moved, changed hands or been nested elsewhere no longer counts as
/// open. Positions are compared for a world-rooted container only; one held by a
/// character travels with them.
/// </summary>
public sealed class OpenedContainerRegistry
{
    private readonly record struct Entry(uint ParentUid, uint TopMostUid, Point3D OpenedAt);

    private readonly Dictionary<uint, Entry> _open = [];

    /// <summary>Distance a world-rooted container may travel before the open view of
    /// it is stale (Source-X uses 3).</summary>
    private const int MaxDriftTiles = 3;

    public int Count => _open.Count;

    public void Clear() => _open.Clear();

    /// <summary>Record that <paramref name="container"/> was just displayed.</summary>
    public void MarkOpened(Item container, Objects.ObjBase? topMost, Point3D openedAt)
    {
        _open[container.Uid.Value] = new Entry(
            container.ContainedIn.Value,
            topMost?.Uid.Value ?? container.Uid.Value,
            openedAt);
    }

    public void Forget(Item container) => _open.Remove(container.Uid.Value);

    /// <summary>
    /// True when <paramref name="container"/> is open for this client AND still hangs
    /// off what it did when it was opened.
    /// </summary>
    /// <param name="topMost">The container's current top-level object.</param>
    /// <param name="topIsCharacter">True when that top-level object is a character —
    /// a carried container moves with its owner, so its position is not compared.</param>
    public bool IsOpen(Item container, Objects.ObjBase? topMost, bool topIsCharacter)
    {
        if (!_open.TryGetValue(container.Uid.Value, out var entry))
            return false;

        uint currentTopMost = topMost?.Uid.Value ?? container.Uid.Value;
        if (entry.TopMostUid != currentTopMost)
            return false;

        // The container must still sit where it did: re-nesting it elsewhere is a
        // different container view even though the uid is unchanged.
        if (entry.ParentUid != container.ContainedIn.Value)
            return false;

        if (topIsCharacter)
            return true;

        var top = topMost as Item;
        var now = top?.Position ?? container.Position;
        return entry.OpenedAt.Map == now.Map &&
               entry.OpenedAt.GetDistanceTo(now) <= MaxDriftTiles;
    }
}
