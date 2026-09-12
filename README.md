# Invicta.Time

Short delays are difficult to get right on Windows. `Thread.Sleep`, `Task.Delay` and anything driven by
`TimeProvider.System`, such as `PeriodicTimer`, are tied to the system clock tick and will not honour a delay
shorter than about 15.6 ms.

Windows 10 version 1803 added the `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` flag to
[`CreateWaitableTimerExW`][createwaitabletimerexw], which brings that floor down to roughly 0.5 ms. Unlike busy
waiting, such timers cost almost nothing and leave power management alone; unlike `timeBeginPeriod`, they do not
change the timer resolution for every other process on the machine. For the background, see
[Perfect sleep function][bearcats], [Windows high-resolution timers][siliceum] and
[Windows Timer Resolution: The Great Rule Change][randomascii].

This library packages those timers as a [`TimeProvider`][timeprovider]. Pass
`HighResolutionTimeProvider.Instance` wherever a `TimeProvider` is accepted, such as `Task.Delay`,
`CancellationTokenSource`, `PeriodicTimer` and `CreateTimer`.

Requires .NET 10 and Windows 10 version 1803 (build 17134) or later; `HighResolutionTimeProvider.IsSupported`
reports whether the current OS qualifies.

## Resolution

Medians from `benchmarks/Invicta.Time.Benchmarks`, on an AMD Ryzen 7 9800X3D running Windows 11 25H2:

| | `TimeProvider.System` | `HighResolutionTimeProvider` |
|---|---|---|
| `Task.Delay(1 ms)` | 15.63 ms | 1.38 ms |
| `PeriodicTimer(1 ms)` tick | 15.66 ms | 1.00 ms |
| One-shot `ITimer` callback at 1 ms | 15.65 ms | 1.39 ms |

The spread either side of those medians is narrow: the 95th percentile is within 0.12 ms of the median in every
case, so the system provider is reliably a tick late rather than occasionally so.

Only `CreateTimer` differs from `TimeProvider.System`. `GetUtcNow` and `GetTimestamp` are already high
resolution, so they are inherited unchanged.

## Design

- **One kernel object:** a single waitable timer and one scheduler thread serve every `ITimer`.
- **Indexed min-heap:** creating, changing and cancelling a timer are all O(log n).
- **Armed for the earliest deadline:** a thread scheduling a sooner timer re-arms the kernel timer itself, so no
  wake-up event is needed.
- **Thread pool callbacks:** queued as `System.Threading.Timer` does, so a starved pool still delays them.
- **Drift-free periods:** missed ticks are skipped rather than fired as a burst.

Behaviour otherwise matches `TimeProvider.System`: `ExecutionContext` flows unless suppressed, callbacks can
overlap, `Change` returns `false` after disposal, `DisposeAsync` waits for running callbacks, and a timer that
is no longer referenced is collected and stops.

## Caveats

- **Late, never early:** a one-shot fires roughly 0.4 ms after its due time, and under 1 ms even in the tail,
  which is kernel granularity plus the thread pool hop.
- **The comparison moves:** the `TimeProvider.System` column depends on the machine-wide timer resolution,
  which any process can raise with `timeBeginPeriod`. While something has raised it, the gap narrows; the
  figures above were taken with the tick at its 15.6 ms default.
- **Thread pool pressure:** a starved pool delays callbacks, exactly as it does for the built-in timers.
- **Unaffected APIs:** `Thread.Sleep`, and `Task.Delay(TimeSpan)` without a provider, keep the system tick.

[createwaitabletimerexw]: https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw
[timeprovider]: https://learn.microsoft.com/dotnet/standard/datetime/timeprovider-overview
[bearcats]: https://blog.bearcats.nl/perfect-sleep-function
[siliceum]: https://siliceum.com/en/blog/post/windows-high-resolution-timers
[randomascii]: https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change
