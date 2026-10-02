# Real-session diagnostics

BetterPerformance records ordinary gameplay without the QA harness. Its diagnostics do not spawn test objects, move characters, force saves, enable cheats or change achievement state. No QA plugin and no other mod is required; installed mods such as ValheimPlus keep their own behavior.

Diagnostics are on by default and every optimization is off by default. Which optimization runs on which role (client, dedicated server or both) is listed in the README table and in each module's page; enable them per installation in `BepInEx/config/jf10r.BetterPerformance.cfg`.

### Capture profile for a play session

Repository defaults record one 300-second capture at 1-second exports. For ordinary play, both the client and the dedicated server switch to continuous recording:

```ini
[Capture]
Enabled = true
AutoStart = true
Continuous = true
DurationSeconds = 1800
IntervalSeconds = 3
QueueCapacity = 16
MaxFileMiB = 64
MaxDirectoryMiB = 1024
PurgeOldestWhenFull = true
MethodTimings = true
```

The `[Diagnostics]` defaults need no change: slow-operation alerts at 20 ms (methods), 100 ms (loop gaps) and 250 ms (save worker), the loot queue observer at 250 ms within 20 m, and the telemetry families listed in the README, each with its own switch.

Each player measures only their own process. Another player's frame times and local loading need BetterPerformance on that computer. With `Relay.SendCapturesEnabled` (and optionally `SendLogEnabled`) on their client and `Relay.AcceptEnabled` on the server, their captures and BepInEx log reach the server as they play; see [capture relay](capture-relay.md).

### Continuous, bounded files

Capture starts automatically in each world session and continues through loading, exploration, combat and saving. Each segment lasts up to 30 minutes. The next segment starts after the previous writer finishes; this can leave a short gap, so coverage is not strictly uninterrupted. Each world session has a `recording_session_id`; each segment has an increasing `segment_index`, a unique capture ID, UTC timestamps and monotonic durations. Client and server have independent session IDs; compare their overlapping UTC windows.

Each installation writes JSONL to `BepInEx/BetterPerformance/captures/`; relayed captures land under `captures/remote/` on the server. Nothing is uploaded elsewhere. Exports aggregate every measured method call between two exports; short peaks remain in the interval maximum. Costly polls can lengthen the export interval as described below.

- One background writer, at most 16 queued records; full queues drop records and report the loss.
- At most 64 MiB per file and a 1 GiB JSONL allowance per installation in this profile. The next file's full allowance must fit. With `PurgeOldestWhenFull` (default on) the plugin deletes its own oldest captures to make room; other files are never touched. Off, recording stops with a BepInEx warning, and `bp_capture start` resumes after making room. Use one process per output directory.
- Archive captures you want to keep to another directory before they are purged.
- Manual stop, collector/writer failure and world exit stop the current recording. A new world can auto-start a new session. No automatic retry loop after an error.
- Shutdown allows two seconds for export. Forced termination, disk errors, queue overflow and a hard file limit can leave incomplete data. A hard-limit segment can lack `capture_end`, including its final loot censor counters; the writer footer and report warnings identify this limitation. Normal duration rotation exports the final counters before closing.

Volume: the last 20-25 segments of this profile wrote about 80-90 MB per hour on each role (range 50-105). That is a measurement of one two-player world, not a limit; the file and directory caps are what bounds disk use.

### Collection-cost safeguards

The collector limits its own work and reports when it reduces collection detail:

- All installed game-method probes retain every measured duration and failure. Only recorder self-timing is sampled, one per 64 valid records. `TimingRecorder` measures aggregation inside the lock, excluding lock acquisition and Harmony dispatch; it is neither the total instrumentation cost nor a bound on its worst case.
- A complete main-thread poll, including snapshot construction and enqueueing, is checked against a soft 2 ms allowance. An overrun doubles the next interval, capped at 10 seconds. Thirty consecutive cheap polls halve it toward the configured minimum. Method/loop histograms keep collecting during this backoff; point-in-time CPU/network/scene context becomes less frequent.
- `collector_previous_poll_cost`, `collector_poll_overruns_total` and `collector_target_interval_at_poll` expose the policy. The current poll's cost can only be reported at the next export. This excludes background serialization and does not include all hooks. Read each record's actual `intervalSeconds` when calculating rates.
- The export worker requests below-normal thread priority so normal-priority game work takes precedence. The `writer_priority` label records whether that request succeeded. It still shares CPU, heap/GC and disk with the game.
- Loot queue scans use small slices and a soft 0.2 ms allowance. A scan exceeding 0.5 ms delays the next scan until at least one second after completion. Overrun and skipped-scan counters make lost coverage explicit.

These are soft safeguards, not a guarantee of negligible cost: a single prefab lookup, lock wait, OS poll, GC or scheduling delay cannot be interrupted by the observer. The hot aggregation path has an offline zero-allocation regression check; serialization still allocates on its worker.

### Slow-operation discovery

Each interval can contain a `slowOperations` array beside the same interval's CPU, GC, socket/Steam queues, peer count, scene occupancy and simulation-radius context. An entry appears when a measured operation reaches its threshold or reports a failed call. At most one entry per known metric per interval is emitted; there are no per-call messages or stack traces.

Coverage includes network update/peers/RPC dispatch, replication, scene and zone updates, near/distant creation, removal, save preparation/call/worker, and collector work. This is an instrumented shortlist, not a profiler of every Valheim or third-party method.

`totalCalls` includes all calls in that interval, not just slow calls. `maxMs` is the largest measured duration; `sumMs` includes all measured calls. `stallsOver50Ms` always counts calls strictly greater than 50 ms, independently of the configurable alert threshold. UTC is the end of the aggregate window, not an exact timestamp for the slowest call.

Method durations are inclusive elapsed time: nested calls overlap and cannot be summed as exclusive CPU cost. A loop gap includes frame pacing and scheduling. The asynchronous save worker's duration is not a main-thread stall. A slow window identifies a place to investigate; sampled CPU, GC or network coincidence does not prove the cause.

### Loot queue clues

The passive observer samples the native near-object candidate queue before the optional loot priority reorder (`ObjectLoading.PrioritizeNearbyLoot`). It inspects up to 128 candidates per scan, keeps at most 1,024 tracked IDs and 1,024 prefab classifications in memory, and exports only aggregates. IDs are never written to the capture.

Available signals include completed local queue waits, pending age, known nearby loot with sampled same-tier nonloot ahead, queue size, partial scans, unknown prefabs, capacity skips and censored tracks. Completed wait is **first local observation to successful local creation**, a lower bound on local queue residence. It is not action-to-loot or server-to-client latency; that is what [loot visibility telemetry](loot-visibility-latency-2026-09-17.md) measures. Wait sums divided by completed counts give the measured mean; no exact loot p95 is available.

Fast objects between scans, portions outside the rotating slice and far objects are not fully observed. Counts across scans can include repeated observations. Untracked creations include nonloot: they are not a count of missed loot. Tracks expire after 30 seconds without observation or 120 seconds total; scene changes and capture closure also censor pending tracks. Censoring means incomplete measurement, not object disappearance. Priority-opportunity counts are sampled lower bounds, not predicted latency savings.

The observer changes no queue order or object state, but its CPU work runs inside the creation batch and therefore consumes part of an enabled object budget. Scan timing excludes skipped-hook checks and completion-postfix overhead.

### Collect and analyze

No console interaction is necessary. If the console is available, `bp_mark lag_loot` adds a local marker without enabling cheats. `bp_capture status` shows the current file; `bp_capture stop` stops continuation and `bp_capture start` starts a new recording. Markers are bounded to 256 per segment and short identifier labels. They do not reach the other process.

After the session, collect that session's JSONL segments from each installation (and `captures/remote/` on the server), including files with warnings. Run:

```powershell
$captures = Get-ChildItem -LiteralPath 'D:/captures/session' -Filter '*.jsonl' -File
python scripts/summarize_capture.py @($captures.FullName) --output 'session-report.md'
```

The report keeps each segment's statistics separate, warns about supplied segment gaps/duplicates and incomplete exports, summarizes each telemetry family present, and ranks the 12 largest retained slow-operation windows with context. All qualifying alerts remain in JSONL even when omitted from that top-12 view. Do not average percentiles or treat consecutive frames as independent experiments. Judge each optimization from its own work counters in ordinary play.
