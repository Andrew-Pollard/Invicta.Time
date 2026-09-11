# HighResolutionTimeProvider

A `System.TimeProvider` whose timers use Windows high-resolution waitable timers
(`CreateWaitableTimerExW` + `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) instead of the ~15.6 ms system clock tick.

```csharp
using HighResolutionTime;

TimeProvider clock = TimeProvider.HighResolution; // C# 14 extension property, alongside TimeProvider.System

await Task.Delay(TimeSpan.FromMilliseconds(1), clock);            // ~1.5 ms instead of ~1–16 ms
using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2), clock);
using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(1), clock);
using ITimer t = clock.CreateTimer(_ => DoWork(), null, TimeSpan.FromMilliseconds(0.5), TimeSpan.FromMilliseconds(1));
```

Requires Windows 10 1803+ and .NET 10. Check `HighResolutionTimeProvider.IsSupported`.

## What changes vs `TimeProvider.System`

Only `CreateTimer`. `GetUtcNow()` (`GetSystemTimePreciseAsFileTime`) and `GetTimestamp()` (`QueryPerformanceCounter`)
are already high resolution, so they're inherited unchanged.

## Measured (samples/LatencyComparison, this machine)

| | p50 | p99 |
|---|---|---|
| `Task.Delay(1 ms)`, `TimeProvider.System` | 10.8 ms | 17.4 ms |
| `Task.Delay(1 ms)`, `HighResolutionTimeProvider` | 1.53 ms | 1.88 ms |
| `PeriodicTimer(1 ms)` interval, `TimeProvider.System` | 11.1 ms | 21.4 ms |
| `PeriodicTimer(1 ms)` interval, `HighResolutionTimeProvider` | 1.02 ms | 1.32 ms |

## Design

- One background scheduler thread (`ThreadPriority.Highest`) and one kernel timer handle, shared by all timers.
- Pending timers sit in an indexed min-heap, so create/change/cancel are O(log n).
- The kernel timer is always armed for the earliest due time. A thread that schedules an earlier timer re-arms it
  directly, so no separate wake-up event is needed.
- Callbacks are queued to the thread pool, as with `System.Threading.Timer`.
- Periodic timers keep a drift-free cadence and skip missed ticks rather than bursting.

Behavior matches `TimeProvider.System`: `ExecutionContext` flows unless suppressed, callbacks can overlap,
`Change` returns `false` after disposal, `DisposeAsync` waits for running callbacks, and a timer that is no longer
referenced gets garbage collected and stops.

## Caveats

- A timer never fires early, but a one-shot typically fires ~0.3–0.9 ms late: that's the kernel's
  high-resolution timer granularity plus the thread-pool hop.
- A starved thread pool delays callbacks, exactly as with the built-in timers.
- `Thread.Sleep`, `Task.Delay(TimeSpan)` without a provider, and `System.Threading.Timer` are unaffected.
