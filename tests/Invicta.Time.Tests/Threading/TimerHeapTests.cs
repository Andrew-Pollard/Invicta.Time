// © 2026 Andrew Pollard. All rights reserved.

using NUnit.Framework;

namespace Invicta.Threading;

internal sealed class TimerHeapTests
{
    [Test]
    public void RandomInsertsAndRemovals_PopInDueOrder()
    {
        var random = new Random(1234);
        var heap = new TimerHeap();
        var live = new List<TimerEntry>();

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
                var entry = new TimerEntry(_ => { }, null, null) { DueTimestamp = random.Next(1000) };
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
        var heap = new TimerHeap();
        heap.Remove(new TimerEntry(_ => { }, null, null));
        Assert.That(heap.Count, Is.Zero);
    }
}
