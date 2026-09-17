# BetterPerformance

Performance diagnostics and experimental, measurable optimizations for Valheim clients and dedicated servers.

**Status: experimental plugin, version 0.4.8. Diagnostics are enabled by default; optimization options are disabled by default. Independent package-copy and exact-map-compression-cache modules extend bulk map serialization. Normal gameplay gains remain workload-dependent; see the implementation and runtime reports.**

### TL;DR: what it improves and who benefits

Diagnostics are always on. Each optimization is one switch, off by default, and reports its own status in the capture. "Measured" means an A/B on the test world or a real session; "expected" means the mechanism is verified in the game code and the gain is not yet measured in play.

| Improvement | Switch | Impact in play | Mod installed on | Helps players without the mod? | Evidence |
| --- | --- | --- | --- | --- | --- |
| Character save: Steam Cloud buffer sized to the payload | `CharacterSave.CloudWriteBufferSizedToPayload` | Save hitch about 2× shorter: the three 100 MiB scratch buffers per save disappear | Your client | No, your own saves only | Expected |
| Character save: map compression cache + bulk map writes | `MapSaving.ExactCompressionCacheEnabled`, `MapSaving.Enabled` | Save hitch about 50 % shorter when your map has not changed | Your client | No, your own saves only | Measured |
| Join: minimap texture cache | `MinimapCache.Enabled`, `Mode=verified` | Joining about 6 s faster, from your third join on | Your client | No | Expected |
| Join: initial loading acceleration | `InitialLoading.Enabled` | Joining about 18 % faster (6 s in one test) | Your client | No | Measured |
| Network: fish and bird update throttling | `Replication.CosmeticResendIntervalEnabled` | About 15 to 20 % less traffic sent by the machine that owns them; everyone receives fewer updates | Client and server | Yes, they receive less traffic | Deferral measured, traffic not yet |
| Network: bird velocity | `Replication.BirdVelocityEnabled` | Birds fly smoothly on other players' screens instead of lagging and jumping | The bird owner's client (usually the host) | Yes, the fix is in the data they receive | Expected |
| Pickup: owner-grant expedite | `Ownership.ExpediteOwnerGrantsEnabled` | Picking up an item another player owns responds sooner (targets the 0.5 s waits) | Server | Yes, no client mod needed | Expected |
| Terraforming: neighbour-save coalescing | `Terrain.CoalesceNeighbourSavesEnabled` | Less lag when terraforming near zone borders: one save per neighbour instead of one per pixel | The machine that owns the terrain (the terraformer's client, or the server) | Yes, for everyone waiting on that frame | Expected |
| Loading: object creation budget and quota | `ObjectLoading.*` | Objects appear more evenly while loading; loot up to 40 % sooner in one test, mixed elsewhere | Your client | No | Measured, workload-dependent |
| Network: local package copy removal | `NetworkMemory.LocalPackageCopyEnabled` | Fewer memory allocations while replicating; no visible change | Client and server | Indirectly | Measured helper only |

Nothing here changes world saves, ownership rules, item duplication guards, the wire format or combat outcomes. Details and sources: the [roadmap](docs/improvement-roadmap-2026-09-15.md), the [0.4.5 validation](docs/validation-0.4.5.md) and the [game update guide](docs/game-update-guide.md).

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

### Object loading

An optional soft time budget spreads scene object creation across frames, with a quota that lets more objects through while time remains, a preparation clock that gives creation its allowance after the near scan/sort, and the separate initial-loading acceleration. Readiness checks, invalid-prefab handling, view distance and save/network formats are unchanged. In the 2026-09-15 session the budget yielded in 1.2 % of creation batches: it only binds on the heavy batches (loading, zone crossings), which are the ones that cause hitches, so its value is measured on those events, not on average frames. Guides: [object budget](docs/object-budget.md), [preparation clock](docs/budget-preparation.md), [initial loading](docs/initial-loading.md), [loot scheduling](docs/loot-latency.md) and the [0.3.0 measurements](docs/loot-results-2026-09-14.md).

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
- [Validation 0.4.6: gameplay probes and counters](docs/validation-0.4.6.md)
- [Play session 2026-09-15: executive report](docs/session-report-2026-09-15.md)
- [Improvement roadmap after the 2026-09-15 session](docs/improvement-roadmap-2026-09-15.md)
- [Research: character save and Steam Cloud](docs/character-save-research-2026-09-15.md), [join caches](docs/join-cache-research-2026-09-15.md), [replication of fish, birds and animals](docs/replication-research-2026-09-15.md), [ownership and second-player latency](docs/ownership-latency-research-2026-09-15.md), [terrain regeneration](docs/terrain-regeneration-research-2026-09-15.md), [server, ValheimPlus map sync and host freeze](docs/server-host-research-2026-09-15.md)
- [Steam Cloud write buffer sizing](docs/cloud-write-optimization.md)
- [Minimap texture cache with shadow verification](docs/minimap-cache.md)
- [Replication cadence and bird velocity](docs/replication-cadence.md)
- [Server owner-grant expedite](docs/ownership-expedite.md)
- [Terrain neighbour-save coalescing](docs/terrain-save-coalescing.md)
- [GUI group sound and mined-drop placement](docs/gui-sound-and-drop-placement.md)
- [Smelter catch-up budget](docs/smelter-catchup-budget.md), [dungeon spawn slicing](docs/dungeon-spawn-slicing.md), [speculative map pre-compression](docs/map-precompression.md)
- [Play session 2026-09-16: executive report](docs/session-report-2026-09-16.md) and the 2026-09-17 research: [clutter](docs/clutter-research-2026-09-17.md), [station catch-up](docs/station-catchup-research-2026-09-17.md), [build-mode placement](docs/placement-research-2026-09-17.md), [ownership release](docs/ownership-release-research-2026-09-17.md), [save pre-compression](docs/save-precompression-research-2026-09-17.md), [dungeon spawn](docs/dungeon-spawn-research-2026-09-17.md)
- [Gameplay-loop timing probes](docs/gameplay-telemetry.md) and [gameplay counters](docs/gameplay-counters.md)
- [Research: shared chest for two players in ValheimPlus](docs/shared-chest-research-2026-09-15.md)
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
