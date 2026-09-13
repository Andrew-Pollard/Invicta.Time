// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

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

    /// <summary>Gets the number of entries in the heap.</summary>
    public int Count { get; private set; }

    /// <summary>Returns the entry that is due soonest, without removing it.</summary>
    /// <returns>The entry, or <see langword="null"/> if the heap is empty.</returns>
    public TimerEntry? Peek() => Count > 0 ? _items[0] : null;

    /// <summary>Adds an entry to the heap.</summary>
    /// <param name="entry">The entry to add, which must not already be in the heap.</param>
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

    /// <summary>Removes the entry that is due soonest. The heap must not be empty.</summary>
    public void RemoveMin() => RemoveAt(0);

    /// <summary>Removes an entry from the heap, if it is in the heap.</summary>
    /// <param name="entry">The entry to remove.</param>
    public void Remove(TimerEntry entry)
    {
        if (entry.HeapIndex >= 0)
        {
            RemoveAt(entry.HeapIndex);
        }
    }

    /// <summary>
    /// Removes the entry at an index, moves the last entry into its place, and sifts that entry up or down to
    /// restore the heap order.
    /// </summary>
    /// <param name="index">The index of the entry to remove.</param>
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

    /// <summary>Moves the entry at an index up the heap until its parent is due no later than it is.</summary>
    /// <param name="index">The index of the entry to move.</param>
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

    /// <summary>Moves the entry at an index down the heap until neither of its children is due before it.</summary>
    /// <param name="index">The index of the entry to move.</param>
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

    /// <summary>Stores an entry at an index and records that index on the entry.</summary>
    /// <param name="entry">The entry to store.</param>
    /// <param name="index">The index to store it at.</param>
    private void Place(TimerEntry entry, int index)
    {
        _items[index] = entry;
        entry.HeapIndex = index;
    }
}
