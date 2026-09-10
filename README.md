# Invicta.Time
A high resolution implementation of `System.TimeProvider` for Windows 10 Version 1803 and later.

Small time delays are difficult to get right on Windows. Calls to functions like `System.Threading.Thread.Sleep()` / `System.Threading.Tasks.Task.Delay()` or uses of `System.TimeProvider.System` (e.g. via `System.Threading.PeriodicTimer`) will not honor delays less than 15 ms.

In Windows 10 version 1803 (`10.0.17134.0`) and later the `Kernel32` function `CreateWaitableTimerExW()` can accept the flag `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`. These high resolution waitable timers can produce delays down to about 0.5 ms.

These timers are advantageous compared to other approaches:
- Unlike busy waiting they consume minimal system resources and play nicely with Windows power management.
- Unlike `timeBeginPeriod()`/`timeEndPeriod()` they do not modify the system-wide timer resolution which can cause side-effects in other applications.

More information about high resolution timing on Windows can be found at:
- https://blog.bearcats.nl/perfect-sleep-function
- https://siliceum.com/en/blog/post/windows-high-resolution-timers
- https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change
- https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw

## `Invicta.Time.TimeProviderExtensions`
This library provides a static `HighResolution` extension property for `TimeProvider` via the `Invicta.Time.TimeProviderExtensions` class. This can be used in the same way as `TimeProvider.System` but on versions of Windows 10 1803 and later will provide better resolution.

### Example
```csharp
internal static class Program
{
    private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(10);

    public static async Task Main(string[] _)
    {
        using PeriodicTimer timer = new(s_interval, TimeProvider.HighResolution);

        while(true)
        {
            // Do something

            await timer.WaitForNextTickAsync();
        }
    }
}
```
