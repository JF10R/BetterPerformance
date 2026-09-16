# BetterPerformance: research and engineering findings

Research date: 2026-09-15 UTC. Target: the locally installed Valheim 1.0.12 client and dedicated server, with BetterNetworking and ValheimPlus alongside BetterPerformance. This report separates source evidence, experiments and hypotheses. It makes no claim of invention priority or universal performance improvement.

This is the initial 0.3.3 research snapshot. Subsequent implementation status, including exact compression reuse and package-copy removal, is maintained in the [0.4.0 disposition](frontier-implementation-0.4.0.md); “not implemented” below describes the initial investigation.

### Executive assessment

The strongest immediate opportunity is removing millions of tiny writes while serializing the character's minimap. A contained implementation can keep native compression, file format, backups, cloud handling and save ordering. The synthetic experiment supports this direction strongly; its desktop CLR numbers must not be presented as game results. See the [synthetic results](frontier-map-benchmark.md) and the separate [runtime validation](frontier-runtime-2026-09-15.md).

The next diagnostic improvements are useful even without replaying the same normal gameplay twice: local graphics-change history; detailed save/RPC stages; observed object service waits; AI cadence, pathfinding and spawning; and main-thread CPU accounting. They narrow attribution. Passive correlation alone cannot prove that a proposed patch would fix a stall.

The most dangerous shortcut would be to accelerate object spawning by ignoring terrain/collider readiness, or to make saves asynchronous by running the original Unity-facing method on a worker. Neither is justified. Recent published reversals in a relevant optimization mod reinforce the need for dependency and persistence invariants. [Valheim Community Patch releases](https://github.com/MidnightsFX/Valheim-Community-Patch/releases).

### What the existing real session establishes

The approximately 23-minute active solo session contained two client loop gaps of about 385 and 446 ms around saves. The server's corresponding save calls took about 73 and 33 ms, while its worker lasted about 295 and 405 ms. These are different measurements: a worker duration is not itself a main-thread pause. A separate server burst had roughly 151 ms RPC and 132 ms replication peaks without a coincident save or GC collection. Nested timings must not be added.

The capture did not show sustained native Steam queue pressure. It also did not include the girlfriend's computer, so it cannot establish that her Wi-Fi, frame scheduling or local object creation is healthy. Ethernet link speed and a shared router do not eliminate latency variation. The old graphics probe observed synchronized simulation state, so it cannot assign those intervals retrospectively to the reported Large → Ultra → Classic selections.

Only one queued-loot completion was captured near startup, with approximately 616 ms of observed wait. This is not a count of pickups, chest transfers or kills. The scanner samples a narrow native creation queue; promptly created loot can escape observation. Likewise, the 1,894 budget exits are loop decisions, not lost objects, saved frames or milliseconds saved.

These limitations motivate the new probes; they do not invalidate the user's observed delays. Local raw evidence remains in the ignored `.qa/real-session-20260915/` directory rather than the public repository.

### Bottlenecks and useful distinctions

| Area | Source-backed mechanism | Decision-enabling observation |
| --- | --- | --- |
| Character saves | A save RPC can synchronously trigger player/map serialization and disk-save work | CharacterSave, MapSerialization, CharacterSaveToDisk, CPU versus elapsed time |
| World saves | Snapshot preparation precedes the worker; some paths join a worker | SavePrepare/SaveClone/SaveCall versus SaveWorker; synchronous trigger context |
| RPC | Receive processing drains messages and executes handlers inline | RpcUpdate versus RpcDispatch and IncomingZdoData; slow intervals |
| Replication | Discovery, sorting, serialization and sends can burst | SyncListBuild versus SendZdos, queue pressure and scene population |
| Objects | Soft creation budget yields; a single object may exceed it | Batch overshoot, individual creation peaks, observed wait after a yield, censoring |
| AI | Aggregate dispatch is scheduled at an accumulated 0.05-second interval; ownership gates work | Batch cost versus wall cadence, supplied dt, repeated frame, list population |
| Navigation/spawns | Path updates and locally owned spawn checks execute in different contexts | PathQuery, PathfindingUpdate, false-return ratio, SpawnListUpdate/SpawnAttempt |

The ownership distinction matters: some AI and spawning work runs on a client owning the relevant entities, even when a dedicated server exists. Improving only the dedicated-server process cannot eliminate every simulation bottleneck.

### Candidate A: bulk expansion of minimap bits

Installed source inspection shows two `BitArray` exploration maps. The native map format writes each bit as a separate boolean byte. Our implementation copies packed words, expands eight bits using a small lookup table, and writes bounded 64 KiB chunks. It preserves the original loops as fallback and changes neither the map envelope nor subsequent native pin serialization/compression. Packed-copy behavior follows the documented BitArray contract. [Microsoft BitArray.CopyTo](https://learn.microsoft.com/en-us/dotnet/api/system.collections.bitarray.copyto?view=net-10.0).

The closest inspected prior art is AsyncSave's bulk map snapshot. Its examined source assumes a primitive array suitable for `Buffer.BlockCopy`; the installed game uses `BitArray`. That code cannot be transplanted unchanged. This is a statement about that source commit, not a compatibility verdict on every published binary. Our implementation was independently written. [AsyncSave MapSnapshot](https://github.com/MidnightsFX/Valheim_AsyncSave/blob/07b90b229bc0d933bbad68511e4fb81280c09e55/AsyncSave/Profile/MapSnapshot.cs).

Cost: approximately 640 KiB retained workspace per thread that uses the fast path, plus native package/compression allocations. The helper is bounded to the verified map size, rejects unsupported inputs before writing, and propagates write failures instead of retrying after partial output. Reentrant calls use the native fallback. A runtime check also matters when another mod patches the boolean writer after our installer runs.

Falsification: compare full decompressed native/optimized payloads, including pins and metadata, on the same live state; reload a disposable saved character; counterbalance native/optimized timings inside one session. Reject if parity fails, the current IL contract is unsupported, or actual save improvement disappears. Microbenchmark speedup is not a frame-rate multiplier.

### Candidate B: avoid repeated work when the map is unchanged

An exact-content cache could compare the compact exploration bits and the complete native-serialized pin/metadata state before expanding/compressing. This differs from a hook-only dirty flag: missed mod mutations must still be detected. World identity, shared exploration and pin order must participate. Return-buffer aliasing must also be addressed.

This is ordinary memoization applied cautiously, not a novel algorithm. It can remove most repeated compression at a stationary base, but may lose value during exploration or frequent pin changes. It trades retained memory and comparison work for avoided expansion/compression. A bound of one or a few snapshots is preferable to an unbounded world cache.

Falsification: mutate each input independently, including through a simulated foreign mod that bypasses our hooks; require exact equality with uncached native output. Measure cache hit rate and saved CPU during a representative exploration session. A high hit rate in an idle benchmark alone is insufficient. **Not implemented.**

### Candidate C: change compression architecture while retaining compatibility

Streaming expanded blocks directly into the native-compatible compressor could avoid an 8 MiB intermediate representation. A native whole-buffer compressor such as libdeflate is a separate option with platform, packaging and memory costs; it is not a drop-in streaming API. Compressed bytes need not match if the same valid payload is reconstructed, but decompression, envelope and CRC correctness must. [libdeflate](https://github.com/ebiggers/libdeflate), [GZip format](https://www.rfc-editor.org/rfc/rfc1952).

A more speculative direction exploits the source's binary alphabet: encode compatible DEFLATE runs/backreferences directly from packed bits without materializing every byte. The constituent coding ideas already exist. The uncertain contribution is a fast, maintainable composition suitable for this exact map format. Dense/random exploration patterns, checksum cost and fragmented pins may erase the benefit. [DEFLATE format](https://www.rfc-editor.org/rfc/rfc1951), [zlib checksum/compression interfaces](https://www.zlib.net/manual.html).

Highest-information experiment: compare a block-streaming prototype against our bulk writer on sparse, dense, random and real disposable map patterns, measuring allocations and total compression time on Unity Mono. Only pursue direct packed-bit encoding if intermediate materialization still dominates. **Neither compression replacement is implemented.**

### Candidate D: amortize world snapshots without losing consistency

SmoothSave spreads world ZDO copying across coroutine steps and tracks intervening changes. This is relevant prior art for snapshot stalls, distinct from character map serialization. [SmoothSave source](https://github.com/blaxxun-boop/SmoothSave/blob/9050fe20974a1efaea384f047214881f9f3fd86a/SmoothSave/SmoothSave.cs).

A more structural approach maintains versioned immutable chunks and publishes a coherent checkpoint. Copy-on-write/RCU and transactional persistence supply useful concepts, but the game must still capture every mutation and retain a valid global save boundary. More workers do not solve missed mutation coverage. Shutdown, overlapping saves, failures and backup rotation need an explicit state machine. [RCU requirements](https://www.kernel.org/doc/html/latest/RCU/Design/Requirements/Requirements.html), [SQLite atomic commit](https://www.sqlite.org/atomiccommit.html).

Falsification: continuously mutate/add/remove objects during snapshots, restart from each checkpoint, compare semantic state, and interrupt at each file-transition stage. This is a much larger correctness project than the initial map patch. **Not implemented; first gather the new snapshot timings.**

### Candidate E: make object service fair without weakening readiness

The new budget measurements expose whether exits cause long observed waits and whether a few expensive creations defeat the soft allowance. An age-aware scheduler could eventually improve worst-case service within native tiers, but a creation request is not proof that its collider, terrain or dependencies are ready. Queue age alone cannot authorize spawning.

The [streaming research](frontier-streaming-research.md) evaluates four alternatives: remove intermediate package copies; cache validated discovery/sort work; add fair service within native constraints; prepare collision data asynchronously with strict readiness gates. Its highest-value first experiment is byte-identical package-copy removal, followed by workload-specific discovery cost measurement.

A pertinent negative result comes from Valheim Community Patch: version 0.26.1 removed an object-rescan optimization after multiplayer churn, missing distant objects and interference with other mods' lists. Its replacement spawn prefix also bypasses the original method body, which would bypass our transpiler there. Co-installation must not be assumed safe merely because both mods improve performance. [Release evidence](https://github.com/MidnightsFX/Valheim-Community-Patch/releases/tag/v0.26.1).

### Candidate F: improve AI responsiveness by protecting service time

Making AI run less often can lower CPU while making behavior worse. Increasing catch-up can overload an already late frame. The useful first distinction is expensive computation versus starvation: compare batch time, wall gap, population, pathfinding work and main-thread CPU. A repeated batch in one rendered frame is a scheduling observation, not proof of defective AI.

Possible later changes include staggering expensive perception/path requests within gameplay deadlines, sharing demonstrably equivalent navigation results, and allocating bounded per-entity service with maximum age. Each requires explicit behavior tests for moving targets, doors, terrain, combat and ownership transfers. A fast pathfinding cache with stale topology is not an optimization success.

The [AI telemetry guide](ai-telemetry.md) documents the implemented observations. No AI logic, update frequency, spawn balance or physics catch-up policy is changed. Controlled injected stalls can validate the sensor; they cannot establish better intelligence.

### Measurement architecture and cost discipline

Keep ordinary play passive: aggregate fixed counters and histograms, sample low-frequency context, and export asynchronously to bounded JSONL segments. Do not log every object/RPC/NPC or take hot-path stack traces. Self-timers cover only portions of the recorder and collector; they are not proof of negligible total patch overhead. Use the isolated harness for replayable stress/overhead experiments, never the user's normal profile.

Main-thread CPU accounting adds discrimination cheaply. It does not measure a particular lock, disk wait or scheduler-ready delay. A low CPU/elapsed ratio can include deliberate pacing. [Windows GetThreadTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadtimes).

The next higher-level observability step is ordinary interaction-to-confirmed-outcome timing, plus a bounded incident timeline. This closes the current gap between queue measurements and the player's experience. GPU/frame presentation counters are conditional on availability; resolution scaling would only address the corresponding rendering bottleneck. See the [observability research](frontier-observability-research.md) for contracts and limitations.

### Priorities, weak directions and open questions

1. Validate and deploy the contained map serialization patch if the runtime safety/performance checks pass; keep other persistence changes separate.
2. Collect the next normal session with the new configuration, save, budget, AI and CPU context. Rank costs by actual incident overlap rather than total counts alone.
3. Prototype exact map reuse or package-copy elimination based on the newly dominant cost.
4. Investigate correlated stalls across the host client, dedicated server and eventually the second client. No host-only trace proves the remote user's input latency.

Weak first directions: blindly raising bandwidth limits, duplicating BetterNetworking's patches, reducing AI cadence globally, forcing physics catch-up, or treating an upscaler as a solution to synchronous saves. These either lack evidence for the observed stall or exchange correctness/responsiveness for lower work.

Open anomalies: why the server had an RPC/replication burst outside saves; how much of the client's map-save pause is expansion versus compression under normal play; which process owns slow AI during two-player exploration; whether post-yield candidates complete promptly under Ultra; and how graphics choices alter active versus synchronized simulation on this exact mod combination.

Why this may still be thinking too small: optimizing individual functions can leave the same synchronous bursts intact. A longer-term architecture would continuously maintain reusable, versioned state and reserve bounded service time for latency-sensitive interactions. That is a redesign of ownership, consistency and scheduling, not a collection of Harmony shortcuts. Current evidence supports a narrow first win and better attribution, not a promised order-of-magnitude improvement to the whole game.
