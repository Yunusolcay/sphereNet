using SphereNet.Core.Types;

namespace SphereNet.Core.Collections;

/// <summary>
/// UID allocation and recycling table. Maps to CWorldThread UID management in Source-X.
/// Items use UIDs with bit 30 set, characters use UIDs without.
/// </summary>
public sealed class UidTable
{
    private readonly Dictionary<uint, object> _objects = [];
    private readonly Queue<int> _freeItemSlots = [];
    private readonly Queue<int> _freeCharSlots = [];
    private int _nextItemIndex = 1;
    private int _nextCharIndex = 1;
    private readonly object _lock = new();

    public int Count => _objects.Count;

    public Serial AllocateItem()
    {
        lock (_lock)
        {
            int index = _freeItemSlots.Count > 0 ? _freeItemSlots.Dequeue() : _nextItemIndex++;
            return Serial.NewItem(index);
        }
    }

    public Serial AllocateChar()
    {
        lock (_lock)
        {
            int index = _freeCharSlots.Count > 0 ? _freeCharSlots.Dequeue() : _nextCharIndex++;
            return Serial.NewChar(index);
        }
    }

    public void Register(Serial uid, object obj)
    {
        lock (_lock)
        {
            _objects[uid.Value] = obj;
        }
    }

    public void Free(Serial uid)
    {
        lock (_lock)
        {
            if (_objects.Remove(uid.Value))
            {
                if (uid.IsItem)
                    _freeItemSlots.Enqueue(uid.Index);
                else if (uid.IsChar)
                    _freeCharSlots.Enqueue(uid.Index);
            }
        }
    }

    /// <summary>
    /// Re-register an object from a temporary serial to its saved serial.
    /// Removes the temp serial WITHOUT recycling its index, registers the new serial,
    /// and advances the next-index counter past the new serial to prevent collisions.
    /// </summary>
    public void ReRegister(Serial oldUid, Serial newUid, object obj)
    {
        lock (_lock)
        {
            // Remove temp serial from tracking (do NOT recycle the index)
            _objects.Remove(oldUid.Value);

            // Register with the saved serial
            _objects[newUid.Value] = obj;

            // Ensure next allocation index is past this serial to prevent future collisions
            int newIndex = newUid.Index + 1;
            if (newUid.IsItem)
            {
                if (newIndex > _nextItemIndex)
                    _nextItemIndex = newIndex;
            }
            else if (newUid.IsChar)
            {
                if (newIndex > _nextCharIndex)
                    _nextCharIndex = newIndex;
            }
        }
    }

    public object? Find(Serial uid)
    {
        lock (_lock)
        {
            return _objects.GetValueOrDefault(uid.Value);
        }
    }

    public T? Find<T>(Serial uid) where T : class
    {
        return Find(uid) as T;
    }

    public bool Exists(Serial uid)
    {
        lock (_lock)
        {
            return _objects.ContainsKey(uid.Value);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _objects.Clear();
            _freeItemSlots.Clear();
            _freeCharSlots.Clear();
            _nextItemIndex = 1;
            _nextCharIndex = 1;
        }
    }
}
