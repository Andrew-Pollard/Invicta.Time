// © 2026 Andrew Pollard. All rights reserved.

using Xunit;

namespace Invicta.Threading;

public class TimerHeapTests
{
    [Fact]
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
                Assert.Equal(-1, victim.HeapIndex);
            }
            else
            {
                var entry = new TimerEntry(_ => { }, null, null) { DueTimestamp = random.Next(1000) };
                heap.Insert(entry);
                live.Add(entry);
            }
        }

        Assert.Equal(live.Count, heap.Count);

        long previous = long.MinValue;
        while (heap.Peek() is { } min)
        {
            Assert.True(min.DueTimestamp >= previous);
            previous = min.DueTimestamp;
            heap.RemoveMin();
        }

        Assert.Equal(0, heap.Count);
    }

    [Fact]
    public void Remove_NotInHeap_IsNoOp()
    {
        var heap = new TimerHeap();
        heap.Remove(new TimerEntry(_ => { }, null, null));
        Assert.Equal(0, heap.Count);
    }
}
