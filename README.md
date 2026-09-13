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

## Usage

Fall back to `TimeProvider.System` where high-resolution timers are unavailable, then pass the provider to any API
that accepts one:

```csharp
TimeProvider provider = HighResolutionTimeProvider.IsSupported
    ? HighResolutionTimeProvider.Instance
    : TimeProvider.System;

await Task.Delay(TimeSpan.FromMilliseconds(2), provider);

using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(5), provider);

using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1), provider);
while (await timer.WaitForNextTickAsync())
{
    // Runs every millisecond, rather than every 15.6 ms.
}
```

Checking `IsSupported` this way also satisfies the platform compatibility analyzer (CA1416).

## Resolution

Medians from `benchmarks/Invicta.Time.Benchmarks` with the displays asleep, on an AMD Ryzen 7 9800X3D running
Windows 11 25H2:

| | `TimeProvider.System` | `HighResolutionTimeProvider` |
|---|---|---|
| `Task.Delay(1 ms)` | 16.14 ms | 1.54 ms |
| `PeriodicTimer(1 ms)` tick | 16.13 ms | 1.00 ms |
| One-shot `ITimer` callback at 1 ms | 16.15 ms | 1.54 ms |
| Periodic `ITimer` callback every 1 ms | 16.13 ms | 1.00 ms |

The 95th percentile is within 0.05 ms of the median in every case.

- **One-shots:** a high-resolution timer expires on roughly the next half-millisecond step after its due time. Each
  benchmark iteration arms its timer the moment the previous wait ends, which is the worst case, so a 1 ms delay
  takes 1.54 ms.
- **Periodic timers:** each tick is just as late, but due times are fixed rather than counted from the previous
  tick, so the average period stays at 1.00 ms.
- **The system provider:** back-to-back 1 ms waits take about 16.1 ms each, not the 15.6 ms of a clock tick.

Only `CreateTimer` differs from `TimeProvider.System`. `GetUtcNow` and `GetTimestamp` are already high
resolution, so they are inherited unchanged.

## Design

- **One kernel object:** a single waitable timer and one scheduler thread serve every `ITimer`.
- **Sorted set:** pending timers are kept in a `SortedSet`, so creating, changing and cancelling a timer are all
  O(log n). Each schedule, including each tick of a periodic timer, allocates about 50 bytes; a 1 ms periodic timer
  allocates about 50 KB a second.
- **Armed for the earliest deadline:** a thread scheduling a sooner timer re-arms the kernel timer itself, so no
  wake-up event is needed.
- **Thread pool callbacks:** queued as `System.Threading.Timer` does, so a starved pool still delays them.
- **Drift-free periods:** missed ticks are skipped rather than fired as a burst.

Behaviour otherwise matches `TimeProvider.System`: `ExecutionContext` flows unless suppressed, callbacks can
overlap, `Change` returns `false` after disposal, `DisposeAsync` waits for running callbacks, and a timer that
is no longer referenced is collected and stops.

## Caveats

- **Late, never early:** a timer fires up to about 0.5 ms after its due time, depending on when it was armed,
  plus the thread pool hop.
- **Other processes' resolution changes:** whenever any process raises or lowers the timer resolution, every
  overdue `TimeProvider.System` timer fires at once. Animating Chromium-based browsers and Electron apps do this in
  bursts once a frame, which pulls the system provider's figures down towards the display's refresh interval,
  such as 5.6 ms at 180 Hz. `HighResolutionTimeProvider` is unaffected, and because its timers leave the resolution
  alone, it does not disturb other processes either. The measurements are in
  [Invicta.TimerInvestigation][timerinvestigation].
- **Thread pool pressure:** a starved pool delays callbacks, exactly as it does for the built-in timers.
- **Unaffected APIs:** `Thread.Sleep`, and `Task.Delay(TimeSpan)` without a provider, keep the system tick.

## Licence

Released under the [MIT License][license]. The repository configuration files are based on other projects'; their
notices are in [THIRD-PARTY-NOTICES.md][notices].

[createwaitabletimerexw]: https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw
[timeprovider]: https://learn.microsoft.com/dotnet/standard/datetime/timeprovider-overview
[bearcats]: https://blog.bearcats.nl/perfect-sleep-function
[siliceum]: https://siliceum.com/en/blog/post/windows-high-resolution-timers
[randomascii]: https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change
[timerinvestigation]: https://github.com/Andrew-Pollard/Invicta.TimerInvestigation
[license]: https://github.com/Andrew-Pollard/Invicta.Time/blob/master/LICENSE
[notices]: https://github.com/Andrew-Pollard/Invicta.Time/blob/master/THIRD-PARTY-NOTICES.md
