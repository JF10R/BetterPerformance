# Passive observability: decisions beyond another timing histogram

### Scope and current evidence

Research only, based on the current source and primary documentation reviewed on 2026-09-14. No probe below was implemented or exercised for this report. No game, server, QA world, administrator trace or achievement-affecting command was launched.

The most useful additions are **main-thread CPU occupancy**, **conditional render timing**, and **ordinary action-to-success measurements**. They distinguish three situations that the current loop gaps alone cannot separate. A small incident recorder can join those signals without recording every frame or packet.

Current source already provides:

- [Plugin.cs](../src/BetterPerformance/Plugin.cs): process-wide CPU deltas, loop gaps, GC collection counts and synchronized simulation distance. A low machine-normalized CPU percentage does not show whether one particular thread is saturated.
- [GraphicsTelemetry.cs](../src/BetterPerformance/GraphicsTelemetry.cs): active graphics settings, raw player preferences, resolution, frame limit, VSync and focus. These explain configuration, not actual GPU execution time.
- [SteamTelemetry.cs](../src/BetterPerformance/SteamTelemetry.cs): native Steam queue/rate/ping gauges, with explicit separation from managed game/mod queues.
- [LootQueueTelemetry.cs](../src/BetterPerformance/LootQueueTelemetry.cs): bounded observations ending at native local object creation. Its queue wait is not the delay from pressing interact to receiving an item or opening a chest.

| Proposed observation | Decision it can change | Implementation uncertainty |
| --- | --- | --- |
| Main-thread CPU versus elapsed time | Optimize computation, or investigate a non-running interval instead | Low API risk; accounting resolution and overhead need validation |
| Selected Unity frame counters | Pursue rendering/resolution changes, presentation pacing, or CPU work | Counter availability depends on the shipped engine/backend |
| Local action start to verified outcome | Determine whether creation improvements actually improve interaction latency | Highest semantic work; requires checking current vanilla completion paths |
| Bounded incident windows and matched comparisons | Identify which subsystem deserves the next controlled experiment | Low storage risk with enforced limits; passive correlation is not causation |

### 1. Add main-thread CPU occupancy, not a fabricated scheduler-wait counter

On the Unity main thread, sample `GetThreadTimes(GetCurrentThread(), ...)` beside the existing approximately one-second process poll. Keep user and kernel cumulative values and their deltas. Pair them with elapsed time from the same sampling window. Report `main_thread_cpu_ms`, `main_thread_cpu_fraction`, poll span and availability. The calling-thread pseudo handle avoids process enumeration, cross-process access and administrator privileges; it must not be handed to the writer thread and treated as the Unity thread. [Microsoft: GetThreadTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadtimes), [GetCurrentThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getcurrentthread).

**Decision rule:** long loop gaps accompanied by high main-thread CPU occupancy make CPU work a stronger candidate. Long gaps with little main-thread CPU shift attention toward presentation/pacing, synchronization, I/O or scheduling. Compare the local client's and server's own main-thread readings in overlapping windows; process CPU minus main-thread CPU can separately indicate worker activity. Do not subtract independently sampled values without matching windows and accounting for measurement resolution.

**Limit:** wall time minus charged CPU time is only a non-running/accounting residual. It does not distinguish a runnable thread denied a CPU from a thread deliberately sleeping, waiting on a lock, awaiting rendering or blocked on I/O. The 100 ns storage unit of Windows thread times is not a promise of 100 ns measurement accuracy. Do not turn `QueryThreadCycleTime` into milliseconds: Microsoft explicitly warns against converting its CPU cycles to elapsed time. [Microsoft: QueryThreadCycleTime](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-querythreadcycletime).

Precise ready-versus-blocked scheduling attribution normally needs richer scheduler events. Do not silently install a kernel tracer as a fallback: Windows trace control has privilege requirements, and NT Kernel Logger control specifically requires administrative privileges or LocalSystem. Under this task's no-admin constraint, return **reason unavailable**, not a scheduler diagnosis. [Microsoft: StartTrace permissions](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew).

### 2. Use a small, capability-checked render probe

Prefer a few `ProfilerRecorder` counters over a full Unity profiler attachment: CPU total frame time, CPU main-thread frame time, CPU render-thread frame time and GPU frame time, only where available. Unity documents release-player support, enumeration of available metrics, validity checks and explicit disposal of recorder resources. Resolve the allowlist once; retain bounded buffers and dispose them when recording ends. [Unity: ProfilerRecorder](https://docs.unity3d.com/ja/6000.0/ScriptReference/Unity.Profiling.ProfilerRecorder.html).

This route is more promising than assuming `FrameTimingManager` is enabled in Valheim. Unity documents that release-build frame timing can be disabled, while attaching selected `ProfilerRecorder` counters can request only the corresponding measurements without globally enabling Frame Timing Stats. GPU measurement itself can add overhead, so make the GPU counter separately optional and measured. This documentation establishes an API route, not proof that every counter works in the installed Valheim build. [Unity: release-build enablement](https://docs.unity3d.com/6000.0/Documentation/Manual/frame-timing-manager-enable.html), [selective recording](https://docs.unity3d.com/6000.0/Documentation/Manual/frame-timing-manager-record-timing-data.html).

**Decision rule:** valid high GPU time with relatively smaller CPU work supports investigating rendering; large presentation waits with a frame cap/VSync support a pacing explanation. High main/render-thread work instead supports a CPU-side rendering or simulation investigation. Network queues and action latency remain independent dimensions; a faster rendered frame does not prove faster loot confirmation.

**Correctness gates:** preserve the counter's declared unit and whether it measures elapsed scope time or actual CPU consumption. Unity's `FrameTiming.cpuFrameTime` API describes milliseconds, whereas profiler metric units must be read from their descriptors; do not blindly reuse a conversion between the APIs. Treat zero, invalid, missing and stale samples separately. If using `GetLatestTimings`, obey its returned count and deduplicate completed-frame timestamps rather than treating repeated reads of the latest frame as new frames. Headless captures report rendering unavailable. [Unity: FrameTiming units](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/FrameTiming-cpuFrameTime.html), [metric unit descriptors](https://docs.unity3d.com/cn/6000.0/ScriptReference/Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderDescription.html), [GetLatestTimings](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/FrameTimingManager.GetLatestTimings.html).

Do not export one `LastValue` per second and call its distribution the frame-time p95. Either aggregate observed distinct frames in bounded histograms or label a sparse sample explicitly. Starting counters can have native collection cost even when export is infrequent.

### 3. Measure ordinary pickup and chest actions to a verified local outcome

The missing outcome is what the player experiences after requesting an interaction. Add passive hooks at a local request boundary and a verified completion boundary, after reading the installed vanilla call paths. Candidate hook families include local pickup/interaction entry, ownership/request handling, local inventory mutation and chest UI binding. Exact methods and overloads are **not established by this report**; existing queue instrumentation does not prove their semantics.

For pickup, the endpoint must establish that the local player's inventory accepted the relevant item/quantity. Destruction of the world drop alone is insufficient: another player, stacking, cleanup or an unrelated change can explain it. Separate manual pickup from automatic pickup and partial stack acceptance. For a chest, distinguish request dispatch, explicit rejection, and the local UI actually binding the intended container inventory. A handler returning is not automatically an opened chest.

Use a fixed table, for example at most 128 outstanding local attempts, with an explicit timeout such as 10 seconds. Keep only the small local correlation state needed to match an outcome, no inventory scan or full inventory snapshot per frame. Repeated/ambiguous requests, disconnects, capture stop and capacity exhaustion become censored or unmatched counts, never invented zero latency. Export counts, outcome categories and bounded histograms; do not export player IDs, item names, RPC payloads, inventory contents or persistent world identifiers.

**Decision rule:** if queue-to-object creation improves but action-to-success remains slow, do not declare the player's lag fixed. High action delay after prompt local request dispatch, together with native queue growth, motivates transport/application servicing analysis. High local delay before dispatch points elsewhere. Client-only elapsed action measurements already help without changing the network protocol or installing the mod on every peer.

Steam's pending/unacknowledged fields and estimated queue time describe its transport, not server action completion; queue time also has documented Nagle-related limits. Keep those gauges as context rather than subtracting ping from action latency and labelling the remainder server compute. Without a verified correlation key and clock model, server-side stage summaries remain corroboration, not an exact decomposition. [Valve: Steam networking status semantics](https://partner.steamgames.com/doc/api/Steamnetworkingtypes).

This proposal borrows the **progress-point** principle from causal profiling: measure a unit of useful work, not only time spent inside a convenient function. It does not propose deploying Coz's intervention mechanism. Coz introduces virtual speedups by delaying other execution; those deliberate pauses conflict with passive normal gameplay and the small-overhead requirement. [Curtsinger and Berger, Coz, SOSP 2015](https://sigops.org/s/conferences/sosp/2015/current/SOSP-2015.pdf).

### 4. Join a bounded incident window, then choose one experiment

Keep a small in-memory ring of already aggregated context, for example 30 one-second windows before and after a naturally observed slow action or severe loop gap. A trigger retains the useful context; it must not launch a test, spawn objects, change settings, replay input, request ownership or invoke achievement APIs. Coalesce overlapping incidents and cap retained incident detail. Preserve full-session outcome counts so selecting only bad incidents does not corrupt failure-rate estimates.

On the same Windows host, add a native `QueryPerformanceCounter` anchor and frequency to each process's capture, then align overlapping windows using that common clock. Continue to carry UTC for presentation. Previous local QA found different origins in Unity Mono `Stopwatch`, so identical-looking managed tick units are insufficient evidence of a shared epoch. QPC is suitable for same-system interval timing; counters from the girlfriend's separate computer must not be subtracted as if they shared a clock. Client-local action durations avoid that requirement. [Microsoft: high-resolution timestamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).

**Decision rule:** a slow action with high client CPU, low GPU work and low native queue pressure suggests prioritizing client computation. Simultaneous low charged CPU in both local processes is evidence against sustained main-thread computation, but still cannot separate scheduler starvation from independent waits. An isolated server worker/save event with otherwise responsive actions should not become a claim of a user-visible freeze.

Natural observations nominate the next experiment; they do not prove cause. For any later authorized optimization comparison, vary one feature at a time, preserve graphics/focus/cap context, balance order, and block by comparable route/world activity. Do not count thousands of correlated frames as thousands of independent repetitions. Use repeated-session or appropriately blocked estimates; with insufficient replication, report observed paired ranges instead of a confident causal percentage. [NIST: randomized blocking](https://www.itl.nist.gov/div898/handbook/pri/section3/pri332.htm), [autocorrelation](https://itl.nist.gov/div898/handbook/eda/section3/eda35c.htm).

### Keep the entire recorder under 100 MB/hour

The limit must include existing capture JSON, optional probes, incident records, configuration events, segment headers and writer footers across **both** local processes. Limiting only the new fields is insufficient.

A conservative proposed allowance is 40,000,000 serialized bytes per rolling hour per recording installation, with an explicit two-installation deployment assumption. That gives an 80 MB/hour pair allowance and margin below 100 MB/hour. Enforce the allowance on actual serialized bytes with bounded accounting that survives segment rotation and process restarts; stop/suppress optional detail and report loss when it is exhausted. A directory cap alone is not a write-rate cap, and a large token-bucket burst allowance can violate a strict rolling-hour requirement. Reserve space for completion/loss records and the accounting metadata itself. More than two monitored installations requires dividing the shared allowance further; concurrent writers to one installation need shared accounting or must be rejected.

Start with one-second aggregated context, fixed histogram storage, at most 128 action tracks, at most 60 incident-context slots and a small optional recorder set. Reuse arrays and avoid stack traces, all-RPC hooks, packet payloads, per-frame file output, disk scans and background polling of every process thread. Measure collector/probe cost and serialized growth separately; small output does not imply small CPU overhead. Back off or disable an optional probe when its own cost becomes material, with coverage loss visible in the report.

Achievement protection follows from the scope: observe the normal game paths without invoking them, changing their return values, toggling developer flags, awarding/removing achievements, or mutating saves. The probes still require compatibility review; passive intent alone is not proof that an incorrectly written Harmony patch is harmless.

### Recommended order

Implement main-thread CPU occupancy first, because it cheaply addresses a major interpretation error in the current process-wide percentage. Investigate the semantic action endpoints next, because they are the strongest acceptance criterion for the user's reported lag. Add capability-checked render counters alongside that work if the shipped player exposes them. Use incident windows to keep these observations actionable and bounded. No new networking tuning is justified merely by collecting these signals.
