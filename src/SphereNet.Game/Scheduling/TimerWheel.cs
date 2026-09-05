using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Scheduling;

/// <summary>
/// Hashed timer wheel for scheduling NPC AI ticks.
/// 256 slots x 100ms = 25.6 second cycle.
/// Schedule is O(1), Advance is O(slot size).
///
/// Each slot entry carries the UID's scheduling generation and its exact deadline,
/// which is what makes cancellation and long timers correct:
/// <list type="bullet">
/// <item><b>Generation.</b> <see cref="Remove"/> cannot reach into a slot, so the
/// cancelled entry stays there. Rescheduling the same NPC then had its new
/// scheduling consumed by that leftover entry, firing early and then never firing
/// at the intended time. A leftover entry now carries a stale generation and is
/// dropped instead.</item>
/// <item><b>Deadline.</b> A slot index alone cannot tell this revolution from the
/// next, so anything scheduled past the 25.6s cycle fired on the aliased slot
/// instead. NpcAI parks idle NPCs 30-60s out, so that was every idle NPC waking up
/// to ~2.3x more often than intended. An entry whose deadline has not arrived is
/// re-parked for another revolution rather than fired.</item>
/// </list>
/// </summary>
public sealed class TimerWheel
{
    private const int SlotCount = 256;
    private const long SlotDurationMs = 100;

    /// <param name="Generation">Matched against <see cref="_scheduled"/> to tell a
    /// live scheduling from one that was cancelled or superseded.</param>
    /// <param name="DeadlineMs">The time the NPC is actually due, independent of
    /// which slot the entry landed in.</param>
    private readonly record struct Entry(Character Npc, long Generation, long DeadlineMs);

    private readonly List<Entry>[] _slots;

    /// <summary>UID to the generation of its live scheduling.</summary>
    private readonly Dictionary<uint, long> _scheduled = [];

    /// <summary>Entries pulled from the slot being processed that are not due yet
    /// and have to go back in. Reused to keep Advance allocation-free.</summary>
    private readonly List<Entry> _deferred = new(64);

    private readonly List<Character> _advanceResult = new(256);
    private long _currentTime;
    private int _currentSlot;
    private long _generationCounter;

    public TimerWheel(long startTimeMs)
    {
        _currentTime = startTimeMs;
        _currentSlot = TimeToSlot(startTimeMs);
        _slots = new List<Entry>[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            _slots[i] = [];
    }

    /// <summary>Schedule an NPC for a future fire time. O(1).</summary>
    public void Schedule(Character npc, long fireTimeMs)
    {
        if (npc.IsDeleted || npc.IsPlayer) return;

        uint uid = npc.Uid.Value;
        // Prevent double scheduling
        if (_scheduled.ContainsKey(uid)) return;

        // Clamp to at least next slot
        if (fireTimeMs <= _currentTime)
            fireTimeMs = _currentTime + SlotDurationMs;

        long generation = ++_generationCounter;
        _scheduled[uid] = generation;

        int slot = SlotForDeadline(fireTimeMs);
        // If fire time lands in the current (already-processed) slot,
        // bump to the next slot — otherwise the NPC waits a full wheel
        // revolution (~25.6s) before firing again.
        if (slot == _currentSlot)
            slot = (_currentSlot + 1) & (SlotCount - 1);
        _slots[slot].Add(new Entry(npc, generation, fireTimeMs));
    }

    /// <summary>
    /// Advance the wheel to the current time.
    /// Returns all NPCs whose timers have fired.
    /// Note: The returned list is reused across calls to avoid GC pressure.
    /// Callers must consume or copy the result before the next Advance() call.
    /// </summary>
    public List<Character> Advance(long nowMs)
    {
        _advanceResult.Clear();

        int targetSlot = TimeToSlot(nowMs);

        // Walk from current slot to target slot
        while (_currentSlot != targetSlot || _currentTime + SlotDurationMs <= nowMs)
        {
            _currentSlot = (_currentSlot + 1) & (SlotCount - 1);
            _currentTime += SlotDurationMs;

            var slot = _slots[_currentSlot];
            _deferred.Clear();

            foreach (var entry in slot)
            {
                uint uid = entry.Npc.Uid.Value;

                // Cancelled, or superseded by a later Schedule: this entry is a
                // leftover and must not consume the live scheduling.
                if (!_scheduled.TryGetValue(uid, out long generation) || generation != entry.Generation)
                    continue;

                // The slot index aliases every 25.6s; the deadline does not.
                if (entry.DeadlineMs > nowMs)
                {
                    _deferred.Add(entry);
                    continue;
                }

                _scheduled.Remove(uid);

                if (!entry.Npc.IsDeleted && !entry.Npc.IsPlayer)
                    _advanceResult.Add(entry.Npc);
            }

            slot.Clear();

            // Re-park anything not due yet. Its slot is recomputed from the
            // deadline, so it lands where it belongs on a later revolution.
            foreach (var entry in _deferred)
                _slots[SlotForDeadline(entry.DeadlineMs)].Add(entry);

            // Safety: don't spin more than full cycle (raised for stress tests)
            if (_advanceResult.Count > 500_000) break;
        }

        _currentTime = nowMs;
        return _advanceResult;
    }

    /// <summary>Remove an NPC from the wheel (e.g. on delete). The slot entry is
    /// left behind and retired by its generation when the slot is next walked.</summary>
    public void Remove(Character npc)
    {
        _scheduled.Remove(npc.Uid.Value);
    }

    /// <summary>Number of NPCs currently scheduled.</summary>
    public int Count => _scheduled.Count;

    /// <summary>The wheel position for a point in time (floor).</summary>
    private static int TimeToSlot(long timeMs)
    {
        return (int)((timeMs / SlotDurationMs) & (SlotCount - 1));
    }

    /// <summary>The slot to park a deadline in, rounded UP so the slot is visited
    /// at or after the deadline. Rounding down puts a deadline of 45,065ms in the
    /// slot visited at 45,000ms, which is 65ms too early — the entry is deferred
    /// there and does not come round again for a full 25.6s revolution.</summary>
    private static int SlotForDeadline(long deadlineMs)
    {
        long slots = (deadlineMs + SlotDurationMs - 1) / SlotDurationMs;
        return (int)(slots & (SlotCount - 1));
    }
}
