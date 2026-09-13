// © 2026 Andrew Pollard. All rights reserved.

using System.Runtime.Versioning;

namespace Invicta.Threading;

/// <summary>
/// A binary min-heap of <see cref="TimerEntry"/> ordered by <see cref="TimerEntry.DueTimestamp"/>. Each entry
/// records its own index so that <see cref="Remove"/> is O(log n), which matters for the common "create, then
/// cancel before it fires" pattern (e.g. <see cref="CancellationTokenSource"/> timeouts). Not thread-safe.
/// </summary>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class TimerHeap
{
    private TimerEntry[] _items = new TimerEntry[16];

    public int Count { get; private set; }

    public TimerEntry? Peek() => Count > 0 ? _items[0] : null;

    public void Insert(TimerEntry entry)
    {
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[Count] = entry;
        entry.HeapIndex = Count;
        Count++;

        SiftUp(entry.HeapIndex);
    }

    public void RemoveMin() => RemoveAt(0);

    public void Remove(TimerEntry entry)
    {
        if (entry.HeapIndex >= 0)
        {
            RemoveAt(entry.HeapIndex);
        }
    }

    private void RemoveAt(int index)
    {
        TimerEntry removed = _items[index];
        removed.HeapIndex = -1;
        Count--;

        if (index != Count)
        {
            TimerEntry last = _items[Count];
            _items[index] = last;
            last.HeapIndex = index;

            if (index > 0 && last.DueTimestamp < _items[(index - 1) / 2].DueTimestamp)
            {
                SiftUp(index);
            }
            else
            {
                SiftDown(index);
            }
        }

        _items[Count] = null!;
    }

    private void SiftUp(int index)
    {
        TimerEntry entry = _items[index];
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (_items[parent].DueTimestamp <= entry.DueTimestamp)
            {
                break;
            }

            Place(_items[parent], index);
            index = parent;
        }

        Place(entry, index);
    }

    private void SiftDown(int index)
    {
        TimerEntry entry = _items[index];
        while (true)
        {
            int child = (2 * index) + 1;
            if (child >= Count)
            {
                break;
            }

            if (child + 1 < Count && _items[child + 1].DueTimestamp < _items[child].DueTimestamp)
            {
                child++;
            }

            if (entry.DueTimestamp <= _items[child].DueTimestamp)
            {
                break;
            }

            Place(_items[child], index);
            index = child;
        }

        Place(entry, index);
    }

    private void Place(TimerEntry entry, int index)
    {
        _items[index] = entry;
        entry.HeapIndex = index;
    }
}
