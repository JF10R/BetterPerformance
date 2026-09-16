# BetterPerformance

Performance diagnostics and experimental, measurable optimizations for Valheim clients and dedicated servers.

**Status: experimental plugin, version 0.4.5. Diagnostics are enabled by default; optimization options are disabled by default. Independent package-copy and exact-map-compression-cache modules extend bulk map serialization. Normal gameplay gains remain workload-dependent; see the implementation and runtime reports.**

### TL;DR: what it improves and what to expect in play

Diagnostics are always on; every optimization below is a separate switch, off by default, and reports its own status in the capture. "Measured" means an A/B on the disposable test world or a real session; "expected" means the mechanism is verified in the game code but the gain is not yet measured in play.

| Improvement | Switch | Expected effect in play | Evidence |
| --- | --- | --- | --- |
| Steam Cloud write buffer sized to the payload | `CharacterSave.CloudWriteBufferSizedToPayload` | Character-save stall drops from 130 to 200 ms toward the map-serialization cost; three fewer 100 MiB allocations and 3 to 4 fewer GC collections per save | Expected ([research](docs/character-save-research-2026-09-15.md)) |
| Exact map compression cache + bulk map serialization | `MapSaving.ExactCompressionCacheEnabled`, `MapSaving.Enabled` | Character saves about 45 to 55 % shorter when the map has not changed | Measured ([0.4.0 results](docs/frontier-runtime-0.4.0.md)) |
| Minimap texture cache with shadow verification | `MinimapCache.Enabled`, `Mode=verified` | About 6 s less per join from the third join on, after two byte-identical verifications | Expected ([research](docs/join-cache-research-2026-09-15.md)) |
| Initial loading acceleration | `InitialLoading.Enabled` | Join about 6 s / 18 % shorter in one isolated pair | Measured ([0.4.3 validation](docs/initial-loading-validation-0.4.3.md)) |
| Replication cadence for fish and birds | `Replication.CosmeticResendIntervalEnabled` | About 15 to 20 % less replicated traffic; no change to fishing, ownership or saves | Measured deferral ratio, traffic not yet ([validation](docs/validation-0.4.5.md)) |
| Bird velocity publication | `Replication.BirdVelocityEnabled` | Remote birds fly smoothly instead of lagging and jumping, also for a player without the plugin | Expected ([research](docs/replication-research-2026-09-15.md)) |
| Server owner-grant expedite | `Ownership.ExpediteOwnerGrantsEnabled` (server) | Shorter pickup delay when the second player picks up host-owned items (the 530 ms tail) | Expected ([research](docs/ownership-latency-research-2026-09-15.md)) |
| Terrain neighbour-save coalescing | `Terrain.CoalesceNeighbourSavesEnabled` | Fewer 50 to 100 ms frames while terraforming; saved terrain data identical | Expected ([research](docs/terrain-regeneration-research-2026-09-15.md)) |
| Object creation budget and quota | `ObjectLoading.*` | Lower creation-batch peaks and, in one workload, loot available about 40 % sooner; mixed elsewhere | Measured, workload-dependent ([loot results](docs/loot-results-2026-09-14.md)) |
| Local replication package copy removal | `NetworkMemory.LocalPackageCopyEnabled` | Fewer temporary allocations during replication; no wire change | Measured helper only ([0.4.0 results](docs/frontier-runtime-0.4.0.md)) |

Not changed by any switch: world saves, ownership rules, item duplication guards, wire format, combat outcomes. The [roadmap](docs/improvement-roadmap-2026-09-15.md) lists what is next and what was rejected, and the [game update guide](docs/game-update-guide.md) explains how to re-verify the plugin after a Valheim update.

The initial focus is measuring a client and dedicated server running on the same computer. The goal is to distinguish simulation stalls, object-loading delays, save pauses, and network backlogs before changing game behavior.

### Implemented diagnostics

- Independent client/server captures with UTC timestamps and monotonic durations.
- Loop and method timing distributions, process CPU/memory, GC activity, reported socket queues, scene instance counts, zone readiness and effective simulation radius.
- Separate timing of save preparation, the save call and the save worker.
- Bounded aggregation and background JSONL export with dropped-record accounting and a per-capture file-size limit.
- Native Steam transport counters, valid resident-memory readings, scenario markers and GC/boundary-crossing loop identification.
- Optional continuous capture with linked segments and a directory allowance; no QA harness required.
- Bounded passive loot-queue observations and structured slow-operation summaries with sampled context.
- Reduced recorder self-measurement, smaller loot scans, collection backoff after costly polls and below-normal export priority.
- Bounded local graphics configuration history, with distinct player preferences, active settings and synchronized simulation distances.
- Object-budget tradeoffs, aggregate AI/pathfinding/spawn observations and Windows main-thread CPU accounting.
- Private/Unity memory, optional sparse render timings, bounded local action outcomes and finer replication/character bottleneck stages.
- Passive client loading timelines and inclusive world-generation/terrain stages; distinguish observed wall loading from native game-time spawn messages. See [loading telemetry](docs/loading-telemetry.md), [loading research](docs/loading-research-0.4.1.md) and the [data-safety audit](docs/data-safety-audit-0.4.1.md).
- Base-simulation, terrain and generation timings (structural wear/support, heightmap rebuilds, terrain operations, crop/station ticks, location and dungeon spawns) with population counts; see [base simulation telemetry](docs/base-simulation-telemetry.md).
- Per-name attribution of object creation cost, serialized replication bytes and routed RPC dispatch, bounded top-N per export; see [attribution telemetry](docs/attribution-telemetry.md).
- Unity engine markers and counters through `ProfilerRecorder` (present/render-thread waits, GC pauses, frame times, draw calls), fixed-step accounting and GC mode; see [engine telemetry](docs/engine-telemetry.md).
- Read-only host facts (priority, affinity, timer resolution, power scheme, shared performance counter), Steam transport path (direct or relayed), online backend, ownership and replication counters; see [host and network telemetry](docs/host-network-telemetry.md).
- A Python report command for comparing captures without changing gameplay, persistence or networking settings.

The first two-process capture cannot establish what a remote client is doing. Measurements from additional clients can be added when needed.

### Experimental object loading

An optional soft time budget spreads scene object creation across updates. Separate 0.3.0 options can raise the count allowance while time remains and favor nearby loot within vanilla object-type tiers. Readiness checks, invalid-prefab handling and save/network formats remain in place. It does not change view distance or move Unity work to another thread.

Read the [object-budget guide and experiment](docs/object-budget.md) before enabling it. The module is separate from diagnostics and can be switched off locally during a session after installation at startup. No BetterNetworking implementation is duplicated.

The [loot-scheduling guide](docs/loot-latency.md) describes the independent options and limits. The [0.3.0 measurements](docs/loot-results-2026-09-14.md) document the four-configuration comparison and uncertainty.

The optional [preparation-clock adjustment](docs/budget-preparation.md) gives creation its allowance after the initial near scan/sort, while measuring that preparation separately. It can improve progress when preparation has exhausted the old whole-batch allowance; it can also increase an individual batch's elapsed time.

### Relationship to other mods

BetterPerformance is an independent project. It is intended to work alongside ValheimPlus, without requiring it.

The diagnostics phase is intended to coexist with BetterNetworking. Queue measurements explicitly retain its adjusted socket results. A possible later networking module may reuse and improve BetterNetworking's implementation; if that happens, overlapping networking patches must not run simultaneously. No BetterNetworking code is included. Isolated headless runs with BetterNetworking 2.3.3 and ValheimPlus 0.10.1.1/0.10.1.2 completed; broader compatibility remains unvalidated.

### Documentation

- [Base simulation, terrain and generation telemetry](docs/base-simulation-telemetry.md)
- [Attribution by prefab and RPC name](docs/attribution-telemetry.md)
- [Engine markers and counters](docs/engine-telemetry.md)
- [Host facts, Steam transport path and ownership counters](docs/host-network-telemetry.md)
- [Report sections added in 0.4.4](docs/report-sections-0.4.4.md)
- [Diagnostics validation 0.4.4: offline gates and isolated runtime session](docs/diagnostics-validation-0.4.4.md)
- [Validation 0.4.5: gates, isolated session and what the next session must confirm](docs/validation-0.4.5.md)
- [Play session 2026-09-15: executive report](docs/session-report-2026-09-15.md)
- [Improvement roadmap after the 2026-09-15 session](docs/improvement-roadmap-2026-09-15.md)
- [Research: character save and Steam Cloud](docs/character-save-research-2026-09-15.md), [join caches](docs/join-cache-research-2026-09-15.md), [replication of fish, birds and animals](docs/replication-research-2026-09-15.md), [ownership and second-player latency](docs/ownership-latency-research-2026-09-15.md), [terrain regeneration](docs/terrain-regeneration-research-2026-09-15.md), [server, ValheimPlus map sync and host freeze](docs/server-host-research-2026-09-15.md)
- [Steam Cloud write buffer sizing](docs/cloud-write-optimization.md)
- [Minimap texture cache with shadow verification](docs/minimap-cache.md)
- [Replication cadence and bird velocity](docs/replication-cadence.md)
- [Server owner-grant expedite](docs/ownership-expedite.md)
- [Terrain neighbour-save coalescing](docs/terrain-save-coalescing.md)
- [Initial loading acceleration: enable, disable and measure](docs/initial-loading.md)
- [Why Valheim joining can take 30+ seconds: research reference](docs/valheim-loading-time-analysis.md)
- [Fast-loading experiments and 0.4.2 diagnostics](docs/fast-join-results-0.4.2.md)
- [Native biome cache findings and safe reuse constraints](docs/native-biome-cache-research.md)
- [Frontier loading research](docs/fast-loading-frontier-2026-09-15.md)
- [Real-session profile, slow operations and loot diagnostics](docs/play-session.md)
- [Configuration history and diagnostic interpretation](docs/configuration-history.md)
- [Frontier research and next experiments](docs/frontier-research-2026-09-15.md)
- [0.4.0 feature disposition and implementation](docs/frontier-implementation-0.4.0.md)
- [0.4.0 runtime results and installed profile](docs/frontier-runtime-0.4.0.md)
- [CPU, RAM and bounded worker research](docs/cpu-memory-parallelism-2026-09-15.md)
- [Local package-copy optimization](docs/local-package-copy.md)
- [Local action outcome measurements](docs/action-telemetry.md)
- [Map serialization and runtime validation](docs/frontier-runtime-2026-09-15.md)
- [Object-budget tradeoff measurements](docs/budget-telemetry.md)
- [AI and spawning telemetry](docs/ai-telemetry.md)
- [Main-thread CPU accounting](docs/thread-cpu-telemetry.md)
- [Logging cost reduction and offline measurements](docs/logging-overhead-2026-09-14.md)
- [Measurement scope and interpretation](docs/measurements.md)
- [Build, installation and capture guide](docs/capture-guide.md)
- [Updating the plugin after a Valheim update](docs/game-update-guide.md)
- [Validation results and remaining checks](docs/validation.md)
- [Repeated-test protocol and uncertainty](docs/repeated-tests.md)
- [Runtime results, including possible overhead](docs/runtime-results-2026-09-14.md)
- [Implemented changes](CHANGELOG.md)
- [Development rules](AGENTS.md)
- [License](LICENSE)

### Development

The plugin targets BepInEx 5 / .NET Framework 4.7.2. Build against your local game installation:

```powershell
dotnet run --project tests/BetterPerformance.Tests -c Release
python -m unittest discover -s tests -p 'test_*.py' -v
dotnet build src/BetterPerformance/BetterPerformance.csproj -c Release '-p:ValheimDir=D:/Steam/steamapps/common/Valheim'
```

The offline checks require .NET SDK 10 and Python 3.10+. CI runs the game-independent tests on Windows and Linux. The [capture guide](docs/capture-guide.md) covers configuration, packaging and interpretation.

Do not commit Valheim assemblies, decompiled game sources, save files, or raw diagnostic captures. Keep game references and test artifacts local. Building and packaging do not install anything or start the game or server.

### Credits and license

Original project files are available under the [MIT License](LICENSE).

The networking investigation draws on [CW-Jesse's BetterNetworking](https://github.com/CW-Jesse/valheim-betternetworking) and the [manchyy fork](https://github.com/manchyy/valheim-betternetworking). Any future code reuse must retain the applicable copyright and license notices, including those of redistributed dependencies.
