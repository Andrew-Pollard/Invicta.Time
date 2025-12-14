using Invicta.Time.Native;
using System.ComponentModel;
using static Invicta.Time.Native.Avrt;

namespace Invicta.Time;

public sealed class WaitableTimerHighResolutionTimeProvider : TimeProvider
{
    public static TimeProvider Instance => _lazy.Value;
    private static readonly Lazy<TimeProvider> _lazy = new(() => new WaitableTimerHighResolutionTimeProvider());

    private WaitableTimerHighResolutionTimeProvider() { }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        uint taskIndex = 0;
        nint task = AvSetMmThreadCharacteristicsW("Games", ref taskIndex);
        if (task == nint.Zero)
        {
            throw new Win32Exception();
        }
        _ = AvSetMmThreadPriority(task, AVRT_PRIORITY.AVRT_PRIORITY_HIGH);

        return new WaitableTimerHighResolutionTimer(callback, state, dueTime, period);
    }
}
