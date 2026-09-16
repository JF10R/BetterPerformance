# Frontier implementation and remaining research

Version 0.4.0 turns the verified, contained opportunities into independent modules and adds measurements for the remaining hypotheses. It does **not** implement every proposed architecture: asynchronous persistence and simulation rewrites still lack the consistency and behavior contracts needed for ordinary saves. The research plan is tracked in `tasks/frontier-implementation.md`.

### Feature disposition

| Research direction | Delivered or evaluated | Remaining gate |
| --- | --- | --- |
| Bulk minimap serialization | Existing 0.3.3 optimization retained; native map format and lifecycle | Broader normal-game compatibility |
| Exact map reuse | New optional complete serialized-input comparison and compressed-output cache | Real exploration hit rate versus retained memory |
| Nested package copies | New optional local `SendZDOs` copy removal; unchanged length/payload/order | Workload-specific allocation volume and latency benefit |
| CPU and RAM | Main-thread CPU plus private commit, resident memory and Unity allocator gauges | Long-session trends and cross-process correlation |
| Render/GPU distinction | Optional sparse completed-frame timing if the engine exposes it | Rendered session; headless tests cannot measure GPU performance |
| Loot and chest interactions | Bounded local request/attempt-to-confirmed-outcome observations | More ordinary-play samples; no remote state without that client's plugin |
| Bottleneck incidents | Offline neighboring-interval report; discovery, network sorting and character batch timers | Correlation is not exclusive CPU attribution or causality |
| Immutable compression workers | Standalone bounded 0/1/2/4-worker prototype, 224 verified batches | Unity latency, cancellation, ordered save commit, co-host contention |
| Streaming/direct packed-bit compression | Architecture evaluated; not shipped | Native format/CRC/pin parity and measured benefit beyond current changes |
| World checkpoint amortization / immutable chunks | Native preparation, clone, worker and joins inspected and measured | Complete mutation coverage, consistent checkpoint, failure/overlap/shutdown recovery |
| Discovery/sort caching | New timing boundaries instead of unvalidated reuse | Shadow equivalence under movement, dirty objects, ownership and other mods |
| Age-aware object scheduling | Existing budget/quota/priority plus service-wait telemetry retained | Mixed-object fairness and unchanged readiness; no evidence justifies a new scheduler yet |
| Preparation consuming the object allowance | New optional allowance beginning after verified leading near preparation, with separate prep/service measurements | Loading throughput versus larger whole-batch peaks under contention |
| Asynchronous collision preparation | Documented engine job boundary investigated | Demonstrated cooking/query bottleneck, collider lifetime and readiness equivalence |
| AI scoring/path reuse/staggering | Existing batch cadence, path and spawn probes plus character batch timing | Pure-data extraction and behavior tests under moving targets/ownership/topology changes |

These alternatives are not cumulative toggles that can safely be enabled together. For example, exact cache hits already remove compression work that a worker or a different compressor would otherwise accelerate. A changed-input miss remains native and still provides measurements for deciding whether further architecture is worthwhile.

### Exact compression cache

`MapSaving.ExactCompressionCacheEnabled` installs the optional cache. It compares every byte **after native map serialization**, including exploration, shared exploration, saved pins and public-position metadata. It avoids compression only; it does not skip native map serialization. No dirty-bit hooks can miss a mod's field mutation.

One entry owns private input and encoded-output copies, bounded to 24 MiB total retained bytes; source inputs above 16 MiB bypass it. A typical 2048-square map has approximately 8 MiB of boolean payload before compression. Old entries become collectible on replacement/world change/shutdown. The retained limit is not a bound on transient heap/GC peak, and cache misses add allocation/copy work. No forced collection is performed.

Installation checks the native writer, array extraction and compressor instruction/call/disposal contracts. Late Harmony changes to those methods trigger native fallback. Supported streams must be distinct, owned, accessible buffers with matching plain writers. Hits preserve length framing, flush behavior and destination writes; write errors propagate without retrying partially written output. The cache does not change backups, cloud saves, save scheduling, file names or object ownership.

Counters prefixed `map_cache_` are process-lifetime cumulative totals except retained bytes. Subtract endpoints; do not sum repeated cumulative samples. Compare hit/miss volume, lookup/store time, native compression time and retained bytes. Avoided input bytes are not an equal-sized reduction in live RAM.

### Measurements without a QA dependency

New diagnostics are embedded in BetterPerformance. The QA harness is only used inside disposable test installations.

- `Diagnostics.ResourceMemoryEnabled`: private committed process bytes, page-fault count and Unity allocated/reserved/unused/managed allocator bytes at the normal capture cadence. These are overlapping views and must not be added. Page faults include soft faults; a rising count does not establish disk paging. No heap scan or GC request.
- `Diagnostics.LocalActionOutcomesEnabled`: fixed-capacity local pickup/container tracker; confirmed outcomes, native false returns, unmatched/ambiguous/censored cases and eligible wait sums/counts. A false inventory return can include partial transfer. See the [action guide](action-telemetry.md).
- `Diagnostics.SparseRenderTimingEnabled`: optional one-element frame-timing sample with freshness checks and explicit unavailable/headless status. It does not enable engine features or produce GPU frame percentiles. CPU frame elapsed time is distinct from charged main-thread CPU.
- `SectorDiscovery`, `ClientReplicationSort`, `ServerReplicationSort`: exact inclusive native stage timings. They are nested within broader replication/scene work and must not be summed as separate CPU costs.
- `CharacterFixedBatch`: one aggregate dispatch boundary selected by the native Character batch name. Includes movement/grounding and other character work; **not** physics-solver or collider-only time.
- `ObjectLoading.BudgetAfterNearPreparation`: optional budget-clock adjustment, not a scan/sort cache. Prep and following service are measured separately; whole-batch duration can exceed the allowance by preparation cost. Native readiness and shared distant allowance remain. See the [preparation guide](budget-preparation.md).
- Offline incident reports retain neighboring intervals, peak stages, phase and CPU context using existing bounded JSONL records. No per-object logs or per-frame stack traces.

The [parallelism investigation](cpu-memory-parallelism-2026-09-15.md) explains why spare logical processors do not make live Unity state safe to process concurrently. Export already runs in the background, and vanilla world saving already has a worker. More workers can increase total CPU and retained input memory even when batch throughput improves.

Idle action categories are omitted only when every observed counter and pending count is zero. A schema label identifies this convention; coverage/unavailability remains explicit. The local deployment uses three-second exports to leave size and collection headroom. Per-call timings and action timestamps remain observed between exports; this is not one game sample every three seconds.

### Validation boundary

Core and current-game package equivalence tests cover changed input, aliasing, offsets, fallback and errors. Full map/save parity and local action observations additionally require Unity runtime tests. Standalone .NET Framework cannot load some of the game's modern interface bodies; those cases are reported as unavailable, not as passing live hooks.

Measured runtime results and the exact installed configuration are recorded separately after the disposable session. A short stationary cache experiment cannot predict normal exploration FPS, Wi-Fi latency or another client's action latency.
