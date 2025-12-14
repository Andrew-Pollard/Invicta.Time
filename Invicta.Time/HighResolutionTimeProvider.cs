using System.Runtime.InteropServices;

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
            return new HighResolutionWaitableTimerTimer(callback, state, dueTime, period);
        }

        else
        {
            return System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
