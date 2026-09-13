// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;

namespace Invicta.Threading;

internal sealed class TimerHeapTests
{
    [Test]
    public void RandomInsertsAndRemovals_PopInDueOrder()
    {
        Random random = new(1234);
        TimerHeap heap = new();
        List<TimerEntry> live = [];

        for (int i = 0; i < 5000; i++)
        {
            if (live.Count > 0 && random.Next(3) == 0)
            {
                TimerEntry victim = live[random.Next(live.Count)];
                heap.Remove(victim);
                live.Remove(victim);

                Assert.That(victim.HeapIndex, Is.EqualTo(-1));
            }
            else
            {
                TimerEntry entry = new(_ => { }, null, null) { DueTimestamp = random.Next(1000) };
                heap.Insert(entry);
                live.Add(entry);
            }
        }

        Assert.That(heap.Count, Is.EqualTo(live.Count));

        long previous = long.MinValue;
        while (heap.Peek() is { } min)
        {
            Assert.That(min.DueTimestamp, Is.GreaterThanOrEqualTo(previous));
            previous = min.DueTimestamp;
            heap.RemoveMin();
        }

        Assert.That(heap.Count, Is.Zero);
    }

    [Test]
    public void Remove_NotInHeap_IsNoOp()
    {
        TimerHeap heap = new();
        heap.Remove(new TimerEntry(_ => { }, null, null));

        Assert.That(heap.Count, Is.Zero);
    }
}
