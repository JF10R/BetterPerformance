# Measurement scope

This document describes the experimental 0.1.0 collector and its interpretation limits. Runtime validation remains pending.

### Objective

Explain delays during multiplayer sessions by measuring the local client and dedicated server. Start with observation; introduce optimizations only after a repeatable bottleneck is identified.

Capture each process separately. A dedicated server running on the same computer is a different process from the client; neither capture alone explains the entire session. A world hosted inside the client requires explicit role labeling rather than pretending that a separate server process exists.

### Signals

| Area | Implemented measurements | Interpretation limits |
| --- | --- | --- |
| Process and update timing | Plugin Update-to-Update gaps; elapsed method timings; process CPU delta and machine-normalized CPU percentage; working set; managed heap estimate; GC collection deltas | Loop gaps include waiting and scheduling, not just CPU work. Server updates are not rendered frames. Mono GC generations may share underlying collections; do not sum them as independent events. |
| Networking | Peer count; sum/max of sampled socket queue API results; negative and unavailable result counts | No raw throughput, compression time/ratio or action latency. BetterNetworking can adjust results, including negative values; the collector preserves and labels them. No resetting of game network counters. |
| Zones and objects | Zone/scene update timings; CreateObjects/RemoveObjects batch timings; scene instance occupancy; false readiness results; effective simulation radius | Readiness counts observe existing calls, not unique failed object spawns. Batch call counts are not object counts. Sampled occupancy is not creation/removal throughput. |
| Saves | ZDOMan.PrepareSave, ZNet.SaveWorld, and ZNet.SaveWorldThread elapsed timings | Timings overlap and must not be summed. SaveWorld can include waiting; SaveWorldThread is not pure disk time. No claim of full end-to-end save duration. |
| Capture quality | Poll/snapshot/aggregation timings, invalid samples, probe failures, dropped export records, writer time and bytes | Self-measurement excludes some Harmony/callback costs. An external baseline comparison is still required. |

Every instrumented method and counter needs validation against the target game build. A timer surrounding a method reports elapsed time, not necessarily exclusive CPU time or the total duration of asynchronous work.

### Capture design constraints

- Use monotonic time for durations and UTC metadata for correlation between the two local processes.
- Identify process role, game build, mod versions, capture settings and sampling interval.
- Aggregate in bounded memory; avoid per-object log lines and unbounded histories.
- Keep export work outside hot update paths and expose dropped samples.
- Keep data local by default. Do not collect player names, Steam IDs, IP addresses, world names, save contents, or RPC payloads for routine timing diagnostics.
- Provide a diagnostics-only mode with no networking or gameplay tuning.
- Validate overhead with instrumentation disabled and enabled under comparable conditions.

Probe availability and mod versions are recorded at capture start. Unsupported method signatures are skipped. Fixed histograms retain counts, sums, maxima and approximate percentile upper bounds using powers-of-two buckets starting at 0.125 ms. Zero-count timing summaries indicate no completed observed calls in that window, not proven zero cost.

Method timings include nested work and allocations and can include other mods' patches. Calls are assigned to the interval in which their finalizer records completion; a background save can therefore cross interval boundaries. Calls still running when the capture closes are not included in its final timing totals.

Snapshot work appears in the next interval; the final snapshot has no successor. `writer_last_write` describes the most recently completed record, not the interval currently being queued. GC and process CPU statistics include BetterPerformance's own work.

Each file uses schema version 1 and contains `start`, `interval`, `capture_end` and `writer_end` JSONL records. Each record contains `labels`, `gauges` with units, and `timings` in milliseconds. The writer completion record reports final drop totals. A missing footer, file-size limit or queue overflow prevents treating the file as a complete capture. Timing percentiles in the report are the worst retained interval bounds, not whole-session percentiles.

No claim of negligible overhead is made before runtime measurements.

### Comparison conditions

Use a repeatable scene and route. Record graphics and simulation-distance settings, active mods, process roles and player count. Compare one change at a time. Keep warm-up/loading separate from steady play, and distinguish runs that include a save from runs that do not.

Use duration percentiles, stall counts, and backlog trends rather than average FPS alone. Repeat observations sufficiently to separate a consistent change from run-to-run variation; do not claim a gain without an explicit baseline.

With only the local client and server instrumented, conclusions about a remote player's input, Wi-Fi path, frame times, and object readiness remain hypotheses. Cross-machine timing would require additional instrumentation and clock alignment.

### Runtime acceptance checks still required

- Produces separate, bounded local captures for the client and dedicated server.
- Labels supported signals, unavailable signals, units and sampling intervals accurately.
- Can correlate a local stall with observed save, loading, object or network activity without claiming causality from correlation alone.
- Reports its own overhead and dropped samples.
- Leaves gameplay, networking settings and save behavior unchanged.
- Complements the existing offline aggregation/export/report checks with runtime verification.
