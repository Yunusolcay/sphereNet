using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Scheduling;

/// <summary>
/// Hashed timer wheel for scheduling NPC AI ticks.
/// 256 slots × 100ms = 25.6 second cycle.
/// Schedule is O(1), Advance is O(slot size).
/// </summary>
public sealed class TimerWheel
{
    private const int SlotCount = 256;
    private const long SlotDurationMs = 100;

    private readonly List<Character>[] _slots;
    private readonly HashSet<uint> _scheduled = [];
    private readonly List<Character> _advanceResult = new(256);
    private long _currentTime;
    private int _currentSlot;

    public TimerWheel(long startTimeMs)
    {
        _currentTime = startTimeMs;
        _currentSlot = TimeToSlot(startTimeMs);
        _slots = new List<Character>[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            _slots[i] = [];
    }

    /// <summary>Schedule an NPC for a future fire time. O(1).</summary>
    public void Schedule(Character npc, long fireTimeMs)
    {
        if (npc.IsDeleted || npc.IsPlayer) return;

        uint uid = npc.Uid.Value;
        // Prevent double scheduling
        if (!_scheduled.Add(uid)) return;

        // Clamp to at least next slot
        if (fireTimeMs <= _currentTime)
            fireTimeMs = _currentTime + SlotDurationMs;

        int slot = TimeToSlot(fireTimeMs);
        // If fire time lands in the current (already-processed) slot,
        // bump to the next slot — otherwise the NPC waits a full wheel
        // revolution (~25.6s) before firing again.
        if (slot == _currentSlot)
            slot = (_currentSlot + 1) & (SlotCount - 1);
        _slots[slot].Add(npc);
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
            foreach (var npc in slot)
            {
                if (!npc.IsDeleted && !npc.IsPlayer)
                    _advanceResult.Add(npc);
                _scheduled.Remove(npc.Uid.Value);
            }
            slot.Clear();

            // Safety: don't spin more than full cycle (raised for stress tests)
            if (_advanceResult.Count > 500_000) break;
        }

        _currentTime = nowMs;
        return _advanceResult;
    }

    /// <summary>Remove an NPC from the wheel (e.g. on delete).</summary>
    public void Remove(Character npc)
    {
        _scheduled.Remove(npc.Uid.Value);
        // Lazy removal — deleted NPCs are filtered in Advance
    }

    /// <summary>Number of NPCs currently scheduled.</summary>
    public int Count => _scheduled.Count;

    private static int TimeToSlot(long timeMs)
    {
        return (int)((timeMs / SlotDurationMs) & (SlotCount - 1));
    }
}
