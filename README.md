# BetterPerformance

Performance diagnostics and experimental, measurable optimizations for Valheim clients and dedicated servers.

**Status: experimental plugin, version 0.4.15, verified against Valheim 1.0.15 (2026-09-23). Diagnostics are enabled by default; optimization options are disabled by default. Independent package-copy and exact-map-compression-cache modules extend bulk map serialization. Normal gameplay gains remain workload-dependent; see the implementation and runtime reports.**

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
| Loading: object creation budget and quota | `ObjectLoading.*` | Objects appear more evenly while loading; loot up to 40 % sooner in one test, mixed elsewhere | Your client | No | Measured, workload-dependent |
| Join and server world load: biome point cache | `BiomeCache.Enabled`, `Mode` | Skips the 4-5 s biome grid generation once an entry has reproduced byte-for-byte; serves nothing before that. |
| Network: local package copy removal | `NetworkMemory.LocalPackageCopyEnabled` | Fewer memory allocations while replicating; no visible change | Client and server | Indirectly | Measured helper only |
| Network: adaptive send window and rate policy | `Network.AdaptiveFlowEnabled` | Replaces BetterNetworking's Queue Size and Send Rate: the ZDO send gate follows each peer's measured link instead of a fixed 10 KB (vanilla) or 32-80 KB (menu); the Steam rate is pinned high only on LAN | Client and server (each shapes its own sending) | Yes, the receiver needs nothing | Mechanism verified; runtime measured on the test world only |
| Network: packet compression | `Network.CompressionEnabled` | Replaces BetterNetworking's compression: framed Deflate to peers on the same plugin version, negotiated per connection | Every peer that should receive compressed data | No, both ends need the plugin; others stay vanilla | Mechanism verified; ratio measured on the test world only |
| Diagnostics: capture relay | `Relay.SendCapturesEnabled`, `Relay.SendLogEnabled`, `Relay.AcceptEnabled` | The server collects the other players' captures (and, if they opt in, their BepInEx log) under `captures/remote` as they play; about 16 KB/s per client, sent only while the link is idle | Sending clients and the server | Not a gameplay change; a vanilla server just ignores the offer | Mirror proven byte-identical offline; live cost measured on the test world only |
| Network: teleport ghost fix | `Replication.SectorInvalidationFixEnabled` | A player who goes through a portal disappears at once on everyone else's screen instead of standing frozen in the portal until they cross another 64 m zone; same for any object that jumps out of a player's area | Server | Yes, no client mod needed | Mechanism verified in the game code; runtime unmeasured |

Nothing here changes world saves, ownership rules, item duplication guards, the wire format or combat outcomes. Details and sources: the roadmap, the 0.4.5 validation and the [game update guide](docs/game-update-guide.md).

The initial focus is measuring a client and dedicated server running on the same computer. The goal is to distinguish simulation stalls, object-loading delays, save pauses, and network backlogs before changing game behavior.

### Implemented diagnostics

- Independent client/server captures with UTC timestamps and monotonic durations.
- Loop and method timing distributions, process CPU/memory, GC activity, reported socket queues, scene instance counts, zone readiness and effective simulation radius.
- Separate timing of save preparation, the save call and the save worker.
- Bounded aggregation and background JSONL export with dropped-record accounting and a per-capture file-size limit.
- Native Steam transport counters, valid resident-memory readings, scenario markers and GC/boundary-crossing loop identification.
- Optional continuous capture with linked segments and a directory allowance; no QA harness required.
- Bounded passive loot-queue observations and structured slow-operation summaries with sampled context.
- Bounded loot-visibility observations: from a mined chunk, felled tree, log or destructible disappearing to its loot being created locally, split into a network and a local-creation leg. Client only; attribution is positional, not by identity.
- Reduced recorder self-measurement, smaller loot scans, collection backoff after costly polls and below-normal export priority.
- Bounded local graphics configuration history, with distinct player preferences, active settings and synchronized simulation distances.
- Object-budget tradeoffs, aggregate AI/pathfinding/spawn observations and Windows main-thread CPU accounting.
- Private/Unity memory, optional sparse render timings, bounded local action outcomes and finer replication/character bottleneck stages.
- Passive client loading timelines and inclusive world-generation/terrain stages; distinguish observed wall loading from native game-time spawn messages. See [loading telemetry](docs/loading-telemetry.md), loading research and the data-safety audit.
- Base-simulation, terrain and generation timings (structural wear/support, heightmap rebuilds, terrain operations, crop/station ticks, location and dungeon spawns) with population counts; see [base simulation telemetry](docs/base-simulation-telemetry.md).
- Per-name attribution of object creation cost, serialized replication bytes and routed RPC dispatch, bounded top-N per export; see [attribution telemetry](docs/attribution-telemetry.md).
- Unity engine markers and counters through `ProfilerRecorder` (present/render-thread waits, GC pauses, frame times, draw calls), fixed-step accounting and GC mode; see [engine telemetry](docs/engine-telemetry.md).
- Read-only host facts (priority, affinity, timer resolution, power scheme, shared performance counter), Steam transport path (direct or relayed), online backend, ownership and replication counters; see [host and network telemetry](docs/host-network-telemetry.md).
- A Python report command for comparing captures without changing gameplay, persistence or networking settings.

The first two-process capture cannot establish what a remote client is doing. Measurements from additional clients can be added when needed.

### Object loading

An optional soft time budget spreads scene object creation across frames, with a quota that lets more objects through while time remains, a preparation clock that gives creation its allowance after the near scan/sort, and the separate initial-loading acceleration. Readiness checks, invalid-prefab handling, view distance and save/network formats are unchanged. In the 2026-09-15 session the budget yielded in 1.2 % of creation batches: it only binds on the heavy batches (loading, zone crossings), which are the ones that cause hitches, so its value is measured on those events, not on average frames. Guides: [object budget](docs/object-budget.md), [preparation clock](docs/budget-preparation.md), [initial loading](docs/initial-loading.md), [loot scheduling](docs/loot-latency.md) and the 0.3.0 measurements.

### Relationship to other mods

BetterPerformance is an independent project. It is intended to work alongside ValheimPlus, without requiring it.

The diagnostics phase is intended to coexist with BetterNetworking. Queue measurements explicitly retain its adjusted socket results. A possible later networking module may reuse and improve BetterNetworking's implementation; if that happens, overlapping networking patches must not run simultaneously. No BetterNetworking code is included. Isolated headless runs with BetterNetworking 2.3.3 and ValheimPlus 0.10.1.1/0.10.1.2 completed; broader compatibility remains unvalidated.

### Documentation

Project documentation only: guides, each optimization module, and each telemetry family. Session reports, research notes, reviews and validation records are kept outside the repository.

- [Capture guide](docs/capture-guide.md)
- [Real-session diagnostics](docs/play-session.md)
- [Measurement scope](docs/measurements.md)
- [Configuration history and diagnostic interpretation — 0.3.3](docs/configuration-history.md)
- [Repeated-test protocol](docs/repeated-tests.md)
- [Updating the plugin after a Valheim update](docs/game-update-guide.md)
- [Experimental object-creation budget](docs/object-budget.md)
- [Creation allowance after near preparation](docs/budget-preparation.md)
- [Experimental initial loading acceleration](docs/initial-loading.md)
- [Experimental loot scheduling — 0.3.0](docs/loot-latency.md)
- [Minimap texture cache](docs/minimap-cache.md)
- [Biome point cache](docs/biome-point-cache.md)
- [Steam cloud write buffer](docs/cloud-write-optimization.md)
- [Local ZDO package copy](docs/local-package-copy.md)
- [Replication cadence](docs/replication-cadence.md)
- [Owner-grant expedite (server side)](docs/ownership-expedite.md)
- [Sector invalidation after a position write](docs/sector-invalidation-fix.md)
- [GUI group sound and mined-drop placement](docs/gui-sound-and-drop-placement.md)
- [Dungeon spawn slicing](docs/dungeon-spawn-slicing.md)
- [Speculative map pre-compression](docs/map-precompression.md)
- [Network flow: adaptive send window and rate policy](docs/network-flow.md)
- [Network compression](docs/network-compression.md)
- [Capture relay: client captures and logs mirrored to the server](docs/capture-relay.md)
- [Smelter: catch-up telemetry (and the removed catch-up budget)](docs/smelter-catchup-budget.md)
- [Terrain: attribution telemetry (and the removed neighbour-save coalescing)](docs/terrain-save-coalescing.md)
- [Client loading timeline](docs/loading-telemetry.md)
- [Mined-loot visibility latency — measurement plan, 2026-09-17](docs/loot-visibility-latency-2026-09-17.md)
- [Local pickup and container outcomes](docs/action-telemetry.md)
- [Attribution telemetry](docs/attribution-telemetry.md)
- [Base simulation, terrain and generation observations](docs/base-simulation-telemetry.md)
- [Budget tradeoff observations](docs/budget-telemetry.md)
- [Engine markers and counters](docs/engine-telemetry.md)
- [Gameplay-loop observations](docs/gameplay-telemetry.md)
- [Gameplay counters](docs/gameplay-counters.md)
- [Host and network path telemetry](docs/host-network-telemetry.md)
- [AI cadence, pathfinding and spawn observations](docs/ai-telemetry.md)
- [Main-thread CPU observation](docs/thread-cpu-telemetry.md)

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
