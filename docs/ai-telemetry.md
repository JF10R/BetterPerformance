# AI cadence, pathfinding and spawn observations

### What is measured

These diagnostics observe existing native calls while a capture is active. They do not change AI scheduling, spawn chances, ownership, paths, simulation distance, saves or achievements. They are installed with `Capture.MethodTimings`; unavailable signatures are reported as unavailable instead of guessed.

| Timing | Native scope | Interpretation |
| --- | --- | --- |
| `AiBatch` | `MonoUpdatersExtra.UpdateAI` | Inclusive copy/dispatch/cleanup time for a complete local AI batch, including nested calls and a small observation prefix |
| `PathQuery` | `Pathfinding.GetPath` | Inclusive query duration; paths and return values are preserved |
| `PathfindingUpdate` | `Pathfinding.UpdatePathfinding` | Inclusive native maintenance call; not a count of rebuilt tiles |
| `SpawnListUpdate` | `SpawnSystem.UpdateSpawnList` | Inclusive evaluation of one native spawn list |
| `SpawnAttempt` | `SpawnSystem.Spawn` | Native spawn-method calls; not a verified creature count or success count |

These scopes can overlap. Do not add their maxima or sums to obtain exclusive CPU time. Failed native calls retain their exceptions and are counted by the timing finalizers.

### Aggregate AI cadence

The batch prefix reads only the two lists' `Count` values, the supplied `dt`, a monotonic timestamp and `Time.frameCount`. It does not enumerate AI instances or build per-NPC dictionaries. No object identifiers, coordinates, prefab names or profiler-name strings are exported.

The current Valheim 1.0.12 caller accumulates its AI timer in `MonoUpdaters.FixedUpdate`, dispatches when that timer reaches 0.05 seconds, passes `dt=0.05`, then subtracts 0.05. Consequently, AI batches must not be described as one per fixed step or one per rendered frame. These values describe the inspected vanilla caller; the telemetry records actual arguments and observed cadence instead of enforcing that schedule.

| Gauge | Meaning |
| --- | --- |
| `ai_batches_observed` | Batch entries observed in this export interval |
| `ai_batches_repeated_frame` | Entries sharing `Time.frameCount` with the preceding observed batch |
| `ai_wall_gap_count`, `ai_wall_gap_sum`, `ai_wall_gap_max` | Wall gaps between successive batch entries; no first-gap sample is invented |
| `ai_supplied_dt_sum`, `ai_supplied_dt_max` | Supplied simulation-step values, converted to milliseconds |
| `ai_input_count_last`, `ai_input_count_max`, `ai_input_count_sum` | Source-list entries; the sum counts repeated observations, not distinct NPCs |
| `ai_scratch_count_last`, `ai_scratch_count_max` | Existing entries in the scratch list before the native copy; normally zero, but retained explicitly |
| `ai_observations_rejected` | Invalid argument/clock observations excluded from cadence aggregates |

The native dispatcher copies the source list into scratch and calls its entries. Source counts include entries whose `BaseAI.UpdateAI` subsequently exits because this process is not the owner. They therefore are neither an authoritative simulation population nor a count of successful AI updates. Failed batches can also terminate before every entry executes. Retained scratch entries may contain duplicates; the probe deliberately does not inspect them.

Repeated-frame batches are a possible indication of multiple fixed updates before presentation, not evidence of poor AI, missed updates or scheduler starvation. Long wall gaps also include pauses, background throttling, game timing choices and operating-system scheduling. Supplied `dt` and elapsed wall time have different meanings and must remain separate.

Export resets interval totals but preserves the preceding clock/frame observation. Thus the first gap in an interval can begin in the preceding interval. Capture stop and a new capture reset that history entirely. `Finish` includes the final partial interval and releases the session reference. Values requiring an actual observation are omitted when no batch/gap was observed.

### Path results and spawn ownership

`path_query_returns_observed` counts normally returned, main-thread path queries. `path_query_false_results` counts their false results. A false result does not by itself diagnose an impossible route: native validation, query options, navigation readiness and mod behavior can affect the result. Calls throwing an exception are represented in `PathQuery.failedCalls`, not included as false returns. A non-main-thread custom caller is omitted from these aggregate result/cadence gauges and increments probe-failure coverage; ordinary generic method timing remains thread-safe.

The inspected `SpawnSystem.UpdateSpawning` path requires both local ownership and a local player. A dedicated server can therefore show zero spawn-list activity while a client executes spawn work. Zero on one process is not proof that world spawning stopped. Some spawning occurs outside these two methods, and neither method's call count proves how many entities reached an interactive state.

### Spawn species and refusals

`SpawnTelemetry` counts native spawn outcomes per prefab on the process that runs `UpdateSpawning` (zone owner with a local player; a dedicated server exports zeros). Observers are void prefixes, postfixes and finalizers; none skips a method or changes a result.

| Gauge suffix | Native decision point | Meaning |
| --- | --- | --- |
| `creatures_day`, `creatures_night` | `Spawn` prefix, `EnvMan.IsNight()` | Natural-list creatures (one per group member), split by the time-of-day flag at spawn |
| `creatures_event` | `Spawn` prefix, `eventSpawner` | Random-event creatures |
| `cap_refused` | `GetNrOfZDOInstances` minus capped `FindBaseSpawnPoint` | `m_maxSpawned` reached; exact because no call sits between the two in `UpdateSpawnList` (asserted by the contract test) |
| `point_searches`, `point_search_failed` | `FindBaseSpawnPoint` | Searches (20 tries each) and searches that found no valid point |
| `points_rejected` | `IsSpawnPointGood` false | Candidate points rejected, including group placement |
| `points_in_player_base_area` | `EffectArea.IsPointInsideArea(PlayerBase)` non-null inside `IsSpawnPointGood` | Rejected by the PlayerBase check; that check runs after biome, blocked, altitude, tilt, center distance and player distance, so a point failing earlier is not counted here |
| `crowded_refused` | `HaveInstanceInRange` true | Another instance closer than `m_spawnDistance` |

Totals are exported as `spawn_<suffix>` every interval, zero included. Per-prefab rows are `spawn_by_prefab_<name>_<suffix>`, non-zero only: 32 named prefabs per capture, the 12 busiest per interval, the rest in `other`. Refusals by chance roll, interval, biome, global key, environment and day/night are not observable without hooking widely used statics; an entry with zero cap checks and zero point searches was stopped by one of them. To read those gates, `spawn_table_mountain` (once per capture, and in the BepInEx log once per world) lists every Mountain entry of `SpawnSystem.m_spawnLists` with its time, environment, key, cap, interval, chance, group, `m_insidePlayerBase`, altitude and radius; `spawn_environment` gives the environment at export. Alt-biome and event spawn lists are not in the table.

Which prefabs carry a PlayerBase area is asset data, not code. ValheimPlus resizes the PlayerBase collider of crafting stations to `workbenchEnemySpawnRange`, or `workbenchRange` when that is 0.

Prefab names are read once per prefab; hooks key a bounded reference map and allocate nothing per call once warm. The `IsPointInsideArea` postfix runs for every caller and returns after one static compare unless a spawn point check is in progress. Each hook has its own status label (`spawn_*_probe_status`); `cap_refused` needs the list and search hooks, the PlayerBase count needs the point hook. Cost is bounded by design, not measured.

### Cost and compatibility limits

Cadence state consists of fixed numeric fields; it has no growing history or retained NPC references. Path-result observation increments two counters. Histograms use the existing bounded `MetricBook`. The collector adds a fixed set of aggregate fields at export, not a log line per NPC or path query. This is a bounded design, not a claim of measured zero overhead.

The hooks preserve arguments, return values and native exceptions. Signature validation covers the exact AI dispatcher, path query overload and nested spawn-data type. Other mods can still change these paths or invoke them differently; status labels and probe failures are part of interpretation. No AI correctness, encounter balance or gameplay-performance improvement is asserted by these diagnostics.

### Verification scope

`AiCadenceTests.Run` covers wall time versus supplied `dt`, same-frame repetition, source/scratch counts, cross-export continuity, invalid/regressing observations, independent snapshots and capture reset. These synthetic tests do not establish AI behavior or spawn success.

`SpawnTallyTests.Run` covers slot bounds, `other` folding, the cap derivation and gauge-key sanitizing. `SpawnGameTests.Run` reads the seven hook signatures, the `SpawnData` fields and the call order from metadata and checks every observer is void with no by-ref result; a standalone CLR cannot load `SpawnSystem`, so installation itself is proven only in Unity.

`AiGameTests.Run` checks the installed game signatures, installs/removes the cadence and path-result hooks in a standalone process, and exercises the inactive path-result hook. It does not invoke the game methods or the cadence prefix: that prefix references native Unity `Time`, whose internal calls cannot execute in standalone CLR. The helper passed against both local client and dedicated-server assemblies; their reference builds also compiled without warnings. Separately authorized isolated runtime validation must report its own results.
