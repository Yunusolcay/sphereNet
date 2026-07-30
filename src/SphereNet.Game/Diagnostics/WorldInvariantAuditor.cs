using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Post-load consistency sweep over a loaded <see cref="GameWorld"/>. Most of the
/// "looks fine in .edit but broken in the client" bugs are cross-structure
/// inconsistencies that a synthetic unit test never reproduces because they only
/// surface with the real script pack and save loaded — an empty-looking bag whose
/// items live in <c>Contents</c> but not the client-facing container index, an item
/// whose resolved type diverges from its definition, a char spawner tracking more
/// live children than its cap. This auditor asserts those invariants so they can be
/// caught by a test against the live data (see the tests) or at startup in a
/// diagnostic mode, instead of by playing the game.
/// </summary>
public static class WorldInvariantAuditor
{
    public enum Kind
    {
        /// <summary>An item sits in its parent's Contents but is absent from the
        /// client-facing container index — the bag renders empty on the client.</summary>
        ContainerIndexMissing,
        /// <summary>An item is in the container index but not in the parent's
        /// authoritative Contents list (or the parent no longer contains it).</summary>
        ContainerIndexOrphan,
        /// <summary>An item's ContainedIn points at a parent that does not exist.</summary>
        ContainerParentMissing,
        /// <summary>The instance's resolved ItemType disagrees with its definition —
        /// the raw type/tdata was never materialised, so raw-type readers misbehave.</summary>
        ItemTypeDivergence,
        /// <summary>A char spawner tracks more live children than its cap allows —
        /// e.g. a reload that re-spawned a fresh quota without re-linking the old.</summary>
        SpawnerOverCount,
        /// <summary>An item's ContainedIn points at a character, but the character
        /// neither wears it, remembers it, nor is dragging it. The item is in
        /// limbo: nobody can see or reach it, yet it is still saved and still
        /// counts against weight — the "it just vanished" report.</summary>
        CharacterLimboItem,
    }

    public readonly record struct Anomaly(Kind Kind, uint Uid, string Detail)
    {
        public override string ToString() => $"[{Kind}] 0x{Uid:X8} {Detail}";
    }

    /// <summary>Run every invariant check over the world and return all anomalies.
    /// An empty list means the load is internally consistent.</summary>
    public static IReadOnlyList<Anomaly> Audit(GameWorld world)
    {
        var anomalies = new List<Anomaly>();
        foreach (var obj in world.GetAllObjects())
        {
            if (obj is Objects.Characters.Character ch)
            {
                if (!ch.IsDeleted)
                    AuditCharacterContainment(world, ch, anomalies);
                continue;
            }
            if (obj is not Item item || item.IsDeleted) continue;
            AuditContainment(world, item, anomalies);
            AuditType(item, anomalies);
            AuditSpawner(item, anomalies);
        }
        return anomalies;
    }

    /// <summary>An item may name a CHARACTER as its container: worn gear, the
    /// memory items the engine equips on the special layer, and the item
    /// currently being dragged all live there. Anything else indexed under a
    /// character is unreachable — not worn, not remembered, not in hand — and is
    /// the server-side shape of an item that "disappeared".</summary>
    private static void AuditCharacterContainment(
        GameWorld world, Objects.Characters.Character ch, List<Anomaly> outList)
    {
        var legitimate = new HashSet<uint>();
        for (int layer = 1; layer < (int)Layer.Qty; layer++)
        {
            var worn = ch.GetEquippedItem((Layer)layer);
            if (worn != null)
                legitimate.Add(worn.Uid.Value);
        }
        foreach (var mem in ch.Memories)
            legitimate.Add(mem.Uid.Value);
        // The lift/drop path parks the dragged item on the character until it
        // lands somewhere, so it is legitimately in neither collection.
        if (ch.TryGetTag("DRAGGING", out string? dragged) &&
            uint.TryParse(dragged, out uint draggedUid))
            legitimate.Add(draggedUid);

        foreach (var child in world.GetContainerContents(ch.Uid))
        {
            if (!legitimate.Contains(child.Uid.Value))
                outList.Add(new Anomaly(Kind.CharacterLimboItem, child.Uid.Value,
                    $"ContainedIn=0x{ch.Uid.Value:X8} ({ch.GetName()}) but not worn, remembered or dragged"));
        }
    }

    private static void AuditContainment(GameWorld world, Item item, List<Anomaly> outList)
    {
        // 1. A contained item must have a resolvable parent.
        if (item.ContainedIn.IsValid && world.FindObject(item.ContainedIn) == null)
        {
            outList.Add(new Anomaly(Kind.ContainerParentMissing, item.Uid.Value,
                $"ContainedIn=0x{item.ContainedIn.Value:X8} not found"));
        }

        // 2. The client view (container index) must equal the server's Contents.
        //    This is the exact class of the "bag empty on client, full in .edit" bug:
        //    items entered Contents while the index was never updated. Both
        //    directions matter and an EMPTY container is not exempt: index
        //    entries under a server-side empty bag are ghost items the client
        //    draws and the player cannot pick up.
        var authoritative = new HashSet<uint>();
        foreach (var child in item.Contents)
            if (!child.IsDeleted)
                authoritative.Add(child.Uid.Value);

        var indexed = new HashSet<uint>();
        foreach (var child in world.GetContainerContents(item.Uid))
            indexed.Add(child.Uid.Value);

        foreach (uint uid in authoritative)
            if (!indexed.Contains(uid))
                outList.Add(new Anomaly(Kind.ContainerIndexMissing, uid,
                    $"in Contents of 0x{item.Uid.Value:X8} but missing from container index (client sees empty)"));

        foreach (uint uid in indexed)
            if (!authoritative.Contains(uid))
                outList.Add(new Anomaly(Kind.ContainerIndexOrphan, uid,
                    $"in container index of 0x{item.Uid.Value:X8} but not in its Contents"));
    }

    private static void AuditType(Item item, List<Anomaly> outList)
    {
        // The instance should behave like a script-created item: the RAW type field
        // must be materialised to the definition's type. The ItemType getter hides a
        // stale raw field by resolving through the def, but the ~20 raw-_type readers
        // (IsStaticBlock, spellbook/book/map/ship/multi/container checks) see the raw
        // field — a Normal raw type on a def-typed item is exactly the "TYPE/MORE not
        // read" bug, invisible through the getter.
        int defIndex = ItemDefHelper.ResolveInstanceDefIndex(item);
        var def = DefinitionLoader.GetItemDef(defIndex);
        if (def == null || def.Type == ItemType.Normal) return;

        if (item.RawType == ItemType.Normal)
            outList.Add(new Anomaly(Kind.ItemTypeDivergence, item.Uid.Value,
                $"raw type unmaterialised (Normal) but def 0x{defIndex:X} is {def.Type}"));
    }

    private static void AuditSpawner(Item item, List<Anomaly> outList)
    {
        var spawn = item.SpawnChar;
        if (spawn == null) return;

        // A spawner may legitimately carry excess for a while after an over-
        // accumulated save (it drains by attrition), so only flag a hard overflow
        // beyond the safety cap — the runaway "one worldgem, hundreds of NPCs" case.
        if (spawn.CurrentCount > spawn.MaxCount && spawn.CurrentCount > 8)
            outList.Add(new Anomaly(Kind.SpawnerOverCount, item.Uid.Value,
                $"spawner tracks {spawn.CurrentCount} live children over cap {spawn.MaxCount}"));
    }
}
