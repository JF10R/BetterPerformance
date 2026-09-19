# Measurement scope

This document describes the collector and its interpretation limits. Version 0.2.0 also reports status and cumulative activity for the separate [optional object-creation budget](object-budget.md). See validation for the tested conditions.

### Objective

Explain delays during multiplayer sessions by measuring the local client and dedicated server. Start with observation; introduce optimizations only after a repeatable bottleneck is identified.

Capture each process separately. A dedicated server running on the same computer is a different process from the client; neither capture alone explains the entire session. A world hosted inside the client requires explicit role labeling rather than pretending that a separate server process exists.

### Signals

| Area | Implemented measurements | Interpretation limits |
| --- | --- | --- |
| Process and update timing | Plugin Update-to-Update gaps; elapsed method timings; process CPU delta and machine-normalized CPU percentage; working set; managed heap estimate; GC collection deltas | Loop gaps include waiting and scheduling, not just CPU work. Server updates are not rendered frames. Mono GC generations may share underlying collections; do not sum them as independent events. |
| Networking | Peer count; adjusted socket queue API results; native Steam pending reliable/unreliable and unacknowledged bytes, estimated rates, ping and queue time; network-peer and RPC update timings | Native counters exclude game/mod managed queues. Steam rates are estimates, not packet capture. No compression time/ratio or action latency. BetterNetworking can adjust socket results, including negative values; the collector preserves them separately. No resetting of game network counters. |
| Zones and objects | Zone/scene update timings; CreateObjects/RemoveObjects batch timings; sorted and distant creation timings; scene instance occupancy; false readiness results; effective simulation radius | Readiness counts observe existing calls, not unique failed object spawns. Batch call counts are not object counts. Sampled occupancy is not creation/removal throughput. |
| Saves | ZDOMan.PrepareSave, ZNet.SaveWorld, and ZNet.SaveWorldThread elapsed timings | Timings overlap and must not be summed. SaveWorld can include waiting; SaveWorldThread is not pure disk time. No claim of full end-to-end save duration. |
| Base simulation and generation | Wear/support, heightmap, terrain operation, station tick, location/dungeon timings; live population counts | Inclusive elapsed per call or batch; counts are list sizes, not per-frame work. Other mods can disable wear, so zero cost is a finding, not a probe failure. See [base simulation telemetry](base-simulation-telemetry.md). |
| Attribution | Top-N creation cost, serialized bytes and routed RPC dispatch by prefab/handler name | Bytes precede compression; names are prefab/handler identifiers; "other" conserves totals. See [attribution telemetry](attribution-telemetry.md). |
| Engine and host | Unity marker/counter samples, fixed steps per frame, GC mode; process priority/affinity, timer resolution, power scheme, Steam transport path, ownership counters | Availability is decided at runtime per metric; headless captures lack render markers; marker overhead is unmeasured. See [engine](engine-telemetry.md) and [host/network](host-network-telemetry.md) telemetry. |
| Capture quality | Poll/snapshot/aggregation timings, invalid samples, probe failures, dropped export records, writer time and bytes; collection backoff and loot cooldown | Self-measurement excludes some Harmony/callback costs. Since 0.3.2, recorder self-timing samples 1/64 valid records inside the lock only. A single instrumented gameplay session cannot isolate total overhead. |

Every instrumented method and counter needs validation against the target game build. A timer surrounding a method reports elapsed time, not necessarily exclusive CPU time or the total duration of asynchronous work.

### Capture design constraints

- Use monotonic time for durations and UTC metadata for correlation between the two local processes.
- Identify process role, game build, mod versions, capture settings and sampling interval.
- Aggregate in bounded memory; avoid per-object log lines and unbounded histories.
- Keep export work outside hot update paths and expose dropped samples.
- Keep data local by default. Do not collect player names, Steam IDs, IP addresses, world names, save contents, or RPC payloads for routine timing diagnostics.
- Provide a diagnostics-only mode with no networking or gameplay tuning.
- Use comparable enabled/disabled conditions when quantifying complete overhead. When normal gameplay cannot be repeated, reduce collection work directly, expose soft overruns and retain uncertainty; do not require a second gameplay pass just to collect useful data.

Probe availability and mod versions are recorded at capture start. Unsupported method signatures are skipped. Fixed histograms retain counts, sums, maxima and approximate percentile upper bounds using powers-of-two buckets starting at 0.125 ms. Zero-count timing summaries indicate no completed observed calls in that window, not proven zero cost.

Method timings include nested work and allocations and can include other mods' patches. Calls are assigned to the interval in which their finalizer records completion; a background save can therefore cross interval boundaries. Calls still running when the capture closes are not included in its final timing totals.

Snapshot work appears in the next interval; the final snapshot has no successor. `writer_last_write` describes the most recently completed record, not the interval currently being queued. GC and process CPU statistics include BetterPerformance's own work.

Each file uses schema version 1 and contains `start`, `interval`, `capture_end` and `writer_end` JSONL records. Each record contains `labels`, `gauges` with units, and `timings` in milliseconds. The writer completion record reports final drop totals. A missing footer, file-size limit or queue overflow prevents treating the file as a complete capture. Timing percentiles in the report are the worst retained interval bounds, not whole-session percentiles.

Version 0.1.1 adds optional `marker` records. `Plugin.Mark("scenario_name")` is a main-thread-only API accepting at most 48 ASCII letters, digits, underscores or hyphens and 256 markers per capture. The preceding aggregate is flushed before changing the phase label. Calls are still assigned on completion: a background call or loop gap may span a marker. `LoopAcrossPhaseBoundary` identifies those loop gaps without removing them from whole-run totals; exclude intervals containing them from phase-specific attribution. Use predefined scenario labels, never player names or identifiers.

`LoopWithGcCollection` is the subset of loop gaps spanning a change in `GC.CollectionCount(0)`. It is a correlation signal, not GC pause duration. It overlaps `LoopInterval` and possibly `LoopAcrossPhaseBoundary`; do not add these totals.

Resident memory uses Windows PSAPI on Windows to avoid Unity Mono's observed zero `WorkingSet64` results. Other platforms use `Process.WorkingSet64`. Zero/failed readings are omitted and labeled unavailable. Resident memory includes shared pages and is not private allocation. See [Microsoft's API reference](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo).

Native Steam sampling calls the client or dedicated-server interface as appropriate. Known ServerSync buffering wrappers are inspected read-only, up to eight layers; unsupported transports remain explicitly unavailable. Pending reliable/unreliable bytes and sent-but-unacknowledged reliable bytes are distinct. Estimated queue time is not action latency; ping may be unavailable or zero on local links. See [Steam's counter definitions](https://partner.steamgames.com/doc/api/Steamnetworkingtypes).

No claim of negligible overhead is made before runtime measurements.

### Comparison conditions

Use a repeatable scene and route. Record graphics and simulation-distance settings, active mods, process roles and player count. Compare one change at a time. Keep warm-up/loading separate from steady play, and distinguish runs that include a save from runs that do not.

Use duration percentiles, stall counts, and backlog trends rather than average FPS alone. Repeat observations sufficiently to separate a consistent change from run-to-run variation; do not claim a gain without an explicit baseline.

With only the local client and server instrumented, conclusions about a remote player's input, Wi-Fi path, frame times, and object readiness remain hypotheses. Cross-machine timing would require additional instrumentation and clock alignment.

### Runtime acceptance checks still required

- Produces separate, bounded local captures for the client and dedicated server.
- Labels supported signals, unavailable signals, units and sampling intervals accurately.
- Can correlate a local stall with observed save, loading, object or network activity without claiming causality from correlation alone.
- Reports measured portions of its own cost, collection backoff and dropped samples without implying complete overhead coverage.
- Leaves gameplay, networking settings and save behavior unchanged.
- Complements the existing offline aggregation/export/report checks with runtime verification.
