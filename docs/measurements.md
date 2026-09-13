# Measurement scope

This document defines the initial diagnostic scope. The collectors described here are not implemented yet.

### Objective

Explain delays during multiplayer sessions by measuring the local client and dedicated server. Start with observation; introduce optimizations only after a repeatable bottleneck is identified.

Capture each process separately. A dedicated server running on the same computer is a different process from the client; neither capture alone explains the entire session. A world hosted inside the client requires explicit role labeling rather than pretending that a separate server process exists.

### Signals

| Area | Candidate measurements | Interpretation limits |
| --- | --- | --- |
| Process and update timing | Frame/update duration distributions, long stalls, process CPU time, memory and GC activity | Average FPS and total CPU utilization can hide a main-thread bottleneck. Server updates are not rendered frames. |
| Networking | Bytes sent/received, reported pending bytes, send deferrals, peer count, compression time and ratio when observable | Socket backlog is not end-to-end action latency. BetterNetworking can change reported queue sizes; record its presence and distinguish raw from adjusted values. |
| Zones and objects | Loading duration, creation/removal counts and timings, pending work and readiness deferrals where accessible | An object received from the network may still wait for its zone before it can appear. Counts alone do not establish cost. |
| Saves | Main-thread preparation, background serialization/write duration, and waits for completion where separable | Timing the SaveWorld call alone does not establish total save duration or disk throughput. |
| Capture quality | Collector execution time, sample count, dropped samples and export duration | Missing samples must be visible. Collection overhead is part of the result. |

Every instrumented method and counter needs validation against the target game build. A timer surrounding a method reports elapsed time, not necessarily exclusive CPU time or the total duration of asynchronous work.

### Capture design constraints

- Use monotonic time for durations and UTC metadata for correlation between the two local processes.
- Identify process role, game build, mod versions, capture settings and sampling interval.
- Aggregate in bounded memory; avoid per-object log lines and unbounded histories.
- Keep export work outside hot update paths and expose dropped samples.
- Keep data local by default. Do not collect player names, Steam IDs, IP addresses, world names, save contents, or RPC payloads for routine timing diagnostics.
- Provide a diagnostics-only mode with no networking or gameplay tuning.
- Validate overhead with instrumentation disabled and enabled under comparable conditions.

These are collection requirements, not claims that overhead is already negligible or that all counters are available.

### Comparison conditions

Use a repeatable scene and route. Record graphics and simulation-distance settings, active mods, process roles and player count. Compare one change at a time. Keep warm-up/loading separate from steady play, and distinguish runs that include a save from runs that do not.

Use duration percentiles, stall counts, and backlog trends rather than average FPS alone. Repeat observations sufficiently to separate a consistent change from run-to-run variation; do not claim a gain without an explicit baseline.

With only the local client and server instrumented, conclusions about a remote player's input, Wi-Fi path, frame times, and object readiness remain hypotheses. Cross-machine timing would require additional instrumentation and clock alignment.

### Completion criteria for the first collector

- Produces separate, bounded local captures for the client and dedicated server.
- Labels supported signals, unavailable signals, units and sampling intervals accurately.
- Can correlate a local stall with observed save, loading, object or network activity without claiming causality from correlation alone.
- Reports its own overhead and dropped samples.
- Leaves gameplay, networking settings and save behavior unchanged.
- Has offline validation for aggregation and export, followed by separately authorized runtime verification.
