# Main-thread CPU observation

On Windows, one normal collection poll reads cumulative kernel and user CPU time for the calling Unity main thread using `GetThreadTimes`. No process enumeration, administrator access, background thread sampling, or ETW session is required. `Reset()` binds the managed thread at capture start; a call from another thread emits `wrong_thread` without sampling that thread or changing the baseline.

The first successful observation is `warmup` and emits no CPU gauges. Later observations emit:

- `main_thread_cpu_delta`: kernel plus user CPU consumed between successful endpoints, in milliseconds.
- `main_thread_cpu_window`: the corresponding independent `Stopwatch` wall interval, in milliseconds.
- `main_thread_cpu_percent`: CPU delta divided by that wall interval, multiplied by 100. This describes one thread and is not divided by logical core count.
- `main_thread_cpu_status`: availability, warmup, invalid delta, native read failure, unsupported platform, missing initialization, or wrong thread.

A failed native read resets both endpoints; the next successful read warms up again. A native binding exception disables further native attempts until the next capture reset. Regressing counters or nonpositive wall intervals emit no gauges and establish a fresh baseline. Storage stays constant and no game objects are retained.

High CPU occupancy alongside slow AI, spawning, or save preparation is evidence that the local main thread consumed CPU during that interval. Low occupancy during a long frame leaves several possible explanations: deliberate frame pacing, blocking I/O, locks, rendering waits, or CPU scheduling competition. **The wall-minus-CPU residual is not a scheduler-wait measurement.** Whole-poll averages can hide short bursts; these gauges do not attribute CPU to one specific nested method.

Windows reports cumulative counters in 100-nanosecond units; that unit is not a guarantee of measurement accuracy. Short windows can show accounting noise, including a CPU percentage slightly above 100, so values are not silently clamped. The native read and following wall timestamp are sequential rather than atomic endpoints. CPU sampling itself contributes a small amount of observed work. No overhead benchmark or in-game improvement is claimed for this addition.

`ThreadCpuWindowTests.Run()` covers warmup versus measured idle, independent endpoints, kernel/user addition, duplicate wall timestamps, counter regressions, recovery, reset, and large cumulative counters. Native availability still requires verification on the target runtime; synthetic tests do not prove Unity compatibility.

API references: [GetThreadTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadtimes) and [GetCurrentThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getcurrentthread). The pseudo handle is obtained on the bound main thread and is neither cached for a worker nor closed.
