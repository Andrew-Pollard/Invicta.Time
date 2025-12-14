using Invicta.Time.Native;
using System.ComponentModel;
using System.Runtime.InteropServices;

using static Invicta.Time.Native.Avrt;

namespace Invicta.Time;

public sealed class HighResolutionTimeProvider : TimeProvider
{
    public static TimeProvider Instance => _lazy.Value;
    private static readonly Lazy<TimeProvider> _lazy = new(() => new HighResolutionTimeProvider());

    private HighResolutionTimeProvider() { }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            //uint taskIndex = 0;
            //nint task = AvSetMmThreadCharacteristicsW("Games", ref taskIndex);
            //if (task == nint.Zero)
            //{
            //    throw new Win32Exception();
            //}
            //_ = AvSetMmThreadPriority(task, AVRT_PRIORITY.AVRT_PRIORITY_HIGH);

            return new HighResolutionWaitableTimerTimer(callback, state, dueTime, period);
        }

        else
        {
            return System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
