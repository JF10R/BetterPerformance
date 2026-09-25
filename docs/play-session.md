# Real-session diagnostics

BetterPerformance 0.3.3 records ordinary gameplay without the QA harness. Its diagnostics do not spawn test objects, move characters, force saves, enable cheats or change achievement state. No QA plugin is required. Other installed mods retain their own behavior.

The installed client additionally enables `MapSaving.Enabled` after isolated native-payload/reload validation; the dedicated server leaves this client-save optimization disabled. Both use the same updated DLL. Graphics changes, save/RPC stages, object-budget tradeoffs, AI/path/spawn observations and Windows main-thread CPU accounting are included automatically. See the runtime results and deployment for measured gains, cost and limitations.

### Prepared client and server profile

The local client and dedicated server use the following capture settings. Repository defaults remain a single 300-second capture with optimization disabled; this profile explicitly enables continuous collection.

```ini
[Capture]
Enabled = true
AutoStart = true
Continuous = true
DurationSeconds = 1800
IntervalSeconds = 2
QueueCapacity = 16
MaxFileMiB = 32
MaxDirectoryMiB = 512
MethodTimings = true

[Diagnostics]
LootQueueEnabled = true
LootQueueScanIntervalMilliseconds = 250
LootQueueRadius = 20
SlowOperationsEnabled = true
SlowMethodMilliseconds = 20
SlowLoopMilliseconds = 100
SlowSaveWorkerMilliseconds = 250
```

The client additionally uses `ObjectLoading.Enabled=true`, `BudgetMilliseconds=4`, `AdaptiveCreationQuota=true`, and `ExpandedCreationQuota=64`. `PrioritizeNearbyLoot=false` remains explicit. The dedicated server uses `ObjectLoading.Enabled=false` and `AdaptiveCreationQuota=false`; its diagnostics remain active. These choices follow the 0.3.0 comparison, which did not establish a direct server benefit or a consistent incremental priority benefit.

BetterNetworking and ValheimPlus remain separate. BetterPerformance does not duplicate BetterNetworking's transport patches. The other player's client does not need BetterPerformance for these local captures; measuring that computer's frame or local loot-creation behavior would require installing it there too.

### Continuous, bounded files

Capture starts automatically in each world session and continues through loading, exploration, combat and saving. Each segment lasts up to 30 minutes. The next segment starts after the previous writer finishes; this can leave a short gap, so coverage is not strictly uninterrupted. Each world session has a `recording_session_id`; each segment has an increasing `segment_index`, a unique capture ID, UTC timestamps and monotonic durations. Client and server have independent session IDs; compare their overlapping UTC windows.

Each installation writes JSONL to `BepInEx/BetterPerformance/captures/`. No uploads occur. Exports normally occur every two seconds and aggregate every measured method call; short peaks remain in the interval maximum. Costly polls can lengthen the export interval as described below. Loot queue scans have their own 250 ms sampling interval.

- One background writer, at most 16 queued records; full queues drop records and report the loss.
- Maximum 32 MiB per file and a 512 MiB JSONL allowance per installation. The next file's full allowance must fit. With `PurgeOldestWhenFull` (default on) the plugin deletes its own oldest captures to make room; other files are never touched. Off, recording stops with a BepInEx warning. Use one process per output directory.
- Archive captures you want to keep to another directory. With the purge off, restart or use `bp_capture start` to resume after making room.
- Manual stop, collector/writer failure and world exit stop the current recording. A new world can auto-start a new session. No automatic retry loop after an error.
- Shutdown allows two seconds for export. Forced termination, disk errors, queue overflow and a hard file limit can leave incomplete data. A hard-limit segment can lack `capture_end`, including its final loot censor counters; the writer footer and report warnings identify this limitation. Normal duration rotation exports the final counters before closing.

The 0.3.1 offline size projection from the earlier client/server captures, enriched with its loot fields, was about **32 MB/hour combined** at two-second exports, or **50 MB/hour** with every eligible slow alert in every export. Version 0.3.2 adds a small fixed set of safeguard counters and can reduce export frequency. These decimal-MB estimates are schema projections, not measured gameplay or a hard hourly rate limiter. Start/marker/footer records add some overhead. The file and directory limits are enforced independently.

### Collection-cost safeguards

Gameplay does not need to be repeated for an A/B comparison. Version 0.3.2 reduces avoidable work directly and reports when it reduces collection detail:

- All installed game-method probes still retain every measured duration and failure. Only recorder self-timing changes to one sample per 64 valid records. `TimingRecorder` now measures aggregation inside the lock, excluding lock acquisition and Harmony dispatch. It is neither total instrumentation cost nor a representative bound on its worst case. Metadata identifies these semantics; do not compare its totals directly to 0.3.1.
- A complete main-thread poll, including snapshot construction and enqueueing, is checked against a soft 2 ms allowance. An overrun doubles the next interval, capped at 10 seconds. Thirty consecutive cheap polls halve it toward the configured minimum. Method/loop histograms continue collecting during this backoff; point-in-time CPU/network/scene context becomes less frequent.
- `collector_previous_poll_cost`, `collector_poll_overruns_total` and `collector_target_interval_at_poll` expose the policy. The current poll's cost can only be reported at the next export. This excludes background serialization and does not include all hooks. Read each record's actual `intervalSeconds` when calculating rates.
- The export worker requests below-normal thread priority so normal-priority game work takes precedence. The `writer_priority` label records whether that request succeeded. It still shares CPU, heap/GC and disk resources with the game.
- Loot scans use smaller slices and a soft 0.2 ms allowance. A scan exceeding 0.5 ms delays the next scan until at least one second after completion. Overrun and skipped-scan counters make lost coverage explicit. Untracked object completions avoid clocks and scene lookups.

These are soft safeguards, not a guarantee of negligible cost: a single prefab lookup, lock wait, OS poll, GC or scheduling delay cannot be interrupted by the observer. They prevent repeated optional work from running at the same frequency after an overrun. The hot aggregation path has an offline zero-allocation regression check; serialization still allocates on its worker. These changes do not certify an FPS impact or explain the entire difference seen in older instrumented runs.

### Slow-operation discovery

Each interval can contain a `slowOperations` array beside the same interval's CPU, GC, socket/Steam queues, peer count, scene occupancy and simulation-radius context. An entry appears when a measured operation reaches its threshold or reports a failed call. At most one entry per known metric per interval is emitted; there are no per-call messages or stack traces.

Default alert thresholds are 20 ms for methods, 100 ms for loop gaps and 250 ms for the save worker. Coverage includes network update/peers/RPC dispatch, replication, scene and zone updates, near/distant creation, removal, save preparation/call/worker, and collector work. This is an instrumented shortlist, not a profiler of every Valheim or third-party method.

`totalCalls` includes all calls in that interval, not just slow calls. `maxMs` is the largest measured duration; `sumMs` includes all measured calls. `stallsOver50Ms` always counts calls strictly greater than 50 ms, independently of the configurable alert threshold. UTC is the end of the aggregate window, not an exact timestamp for the slowest call.

Method durations are inclusive elapsed time: nested calls overlap and cannot be summed as exclusive CPU cost. A loop gap includes frame pacing and scheduling. The asynchronous save worker's duration is not a main-thread stall. A slow window identifies a place to investigate; sampled CPU, GC or network coincidence does not prove the cause. Instrumentation itself has a cost, which the next real session still needs to characterize.

### Loot clues with priority disabled

The passive observer samples the native near-object candidate queue before our optional priority reorder. It inspects up to 128 candidates per scan under a soft 0.2 ms allowance, keeps at most 1,024 tracked IDs and 1,024 prefab classifications in memory, and exports only aggregates. IDs are never written to the capture.

Available signals include completed local queue waits, pending age, known nearby loot with sampled same-tier nonloot ahead, queue size, partial scans, unknown prefabs, capacity skips and censored tracks. Completed wait is **first local observation to successful local creation**, a lower bound on local queue residence. It is not action-to-loot or server-to-client latency. Wait sums divided by completed counts give the measured mean; no exact loot p95 is available.

Fast objects between scans, portions outside the rotating slice and far objects are not fully observed. Counts across scans can include repeated observations. Untracked creations include nonloot: they are not a count of missed loot. Tracks expire after 30 seconds without observation or 120 seconds total; scene changes and capture closure also censor pending tracks. Censoring means incomplete measurement, not object disappearance. Priority-opportunity counts are sampled lower bounds, not predicted latency savings.

The observer changes no queue order or object state, but its CPU work runs inside the creation batch and therefore consumes part of an enabled soft budget. Scan timing excludes skipped-hook checks and completion-postfix overhead. Compare equivalent configurations before claiming a gain.

### Collect and analyze

No console interaction is necessary. If the console is already available, `bp_mark lag_loot` adds a local marker without enabling cheats. `bp_capture status` shows the current file; `bp_capture stop` stops continuation. Markers are bounded to 256 per segment and short identifier labels. They do not reach the other process.

After the session, collect that session's JSONL segments from both installations, including files with warnings. Run:

```powershell
$captures = Get-ChildItem -LiteralPath 'D:/captures/session' -Filter '*.jsonl' -File
python scripts/summarize_capture.py @($captures.FullName) --output 'session-report.md'
```

The report keeps each segment's statistics separate, warns about supplied segment gaps/duplicates and incomplete exports, summarizes loot coverage, and ranks the 12 largest retained slow-operation windows with context. All qualifying alerts remain in JSONL even when omitted from that top-12 view. Do not average percentiles or treat consecutive frames as independent experiments. The report cannot establish the other player's local experience without that client's measurements.
