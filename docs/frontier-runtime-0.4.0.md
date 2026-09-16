# BetterPerformance 0.4.0: runtime results and installation

Validated 2026-09-15 against the installed Valheim 1.0.12 client and dedicated server, Unity 6000.0.75f1. The final DLL is installed in both normal installations; no QA DLL is active there. Source and documentation remain English.

### What shipped

- Independent local replication-package copy removal on client and server.
- Bulk minimap serialization retained, plus exact compression cache on the client.
- Client creation allowance after leading near preparation, alongside the existing 4 ms budget and adaptive quota. Loot priority remains off.
- Embedded memory, action, preparation/service, discovery/sort and character-batch measurements. Existing graphics-change, save, AI, path, spawn and network probes remain.
- Three-second exports, continuous 30-minute segments, 32 MiB per file and 512 MiB directory allowance per installation. No QA plugin or manual capture command is needed for ordinary play.

All optimization defaults remain off in a newly generated configuration. The locally installed settings above are explicitly enabled for this user's next session. The [feature disposition](frontier-implementation-0.4.0.md) covers every research direction and separates implemented modules from unshipped architectural hypotheses.

### Test design and limitations

Two disposable client/server sessions used the same temporary-world fixture and character `perf01`, hidden graphics-free mode, sound disabled and explicit 30 Hz pacing. BetterNetworking 2.3.3, ValheimPlus 0.10.1.2, PlantEverything 1.21.2 and PlantEasily 2.2.0 were present. Effective near/far simulation distance was 5/2. This is not a rendered GPU/FPS or two-physical-client LAN test.

The first session used the original creation-budget clock. The final session used the new preparation clock, sparse idle-action export and final lifecycle fixes. These are sequential starts, not a controlled randomized startup comparison. Between sessions, Windows reported 84.41% and 92.09% whole-machine CPU in two samples while no Valheim process was running. The user subsequently confirmed playing Wardogs concurrently and identified it as the CPU load. Contention, cold code, world initialization and cache warmth limit causal attribution. Other applications were not changed or stopped.

Within each session, unchanged-map and full-character saves used eight alternating paired blocks with cache off/on in the same binary. Bulk map serialization stayed enabled on both sides: results are **additional to 0.3.3**, not comparisons against fully vanilla serialization. No forced GC occurred during these comparisons.

### Save results

| Session / operation | Cache-off median ms | Cache-hit median ms | Paired mean saved ms | Exploratory paired 95% t interval, saved ms |
| --- | ---: | ---: | ---: | ---: |
| Initial / full character save | 114.57 | 62.44 | 62.94 | 30.45 to 95.43 |
| Initial / map serialization | 92.97 | 33.49 | 69.80 | 29.03 to 110.56 |
| Final / full character save | 135.49 | 61.14 | 91.22 | 23.14 to 159.30 |
| Final / map serialization | 102.02 | 49.03 | 41.98 | 12.58 to 71.39 |

Full-save medians fell about 45% and 55% in these repeated unchanged-map workloads. Each interval uses eight within-session technical pairs; it is not a population interval for arbitrary gameplay, independent players or future worlds. The small samples are noisy and not a guarantee. A changed map is a cache miss and still performs native compression plus cache-store work.

Both sessions verified full decompressed map equality across ordinary input, changed local/shared exploration bits, added/edited/removed pins and repeated hits. The disposable character was saved and reloaded, and its full map payload matched. The final session observed 27 hits and 8 deliberate misses with no cache fallback. Retained cache bytes were 8,396,852 (about 8.01 MiB); the hard retained-entry limit is 24 MiB. Transient allocation/GC peaks can be larger. This is a CPU/allocation trade for retained RAM, not proof that total process RAM fell.

World snapshot preparation, world-save worker joins, backups and cloud persistence are unchanged. This release does not eliminate every world-save pause or make persistence fully asynchronous.

### Package copies and RAM

Actual client/server replication completed with no copy failures or compatibility conflicts. Excluding the dedicated helper experiment, the final client avoided about 2.63 MB of intermediate array payload and the server about 0.41 MB during this short run. These are structurally calculated payload-allocation bytes from successful copies; they exclude array headers and do not equal reduced resident memory.

A separate Unity helper experiment compared 2,000 copies of a 4 KiB owned package per trial, eight alternating pairs. Native-fallback and optimized encoded bytes matched before and after every trial. Median time was 4.028 ms versus 0.578 ms per 2,000 copies. The paired mean delta was -19.399 ms, with an exploratory 95% t interval of -41.406 to +2.609 ms; skewed contention/outliers and small samples make the timing estimate uncertain. Compatibility-scope checks, full `SendZDOs`, transport, other peers and rendering are outside this helper measurement. It is not an 86% networking/FPS claim.

The optional Mono allocation API returned zero on both paths even though native `GetArray` creates arrays. Those allocation readings are **invalid/unavailable**, not evidence of zero allocation. The fixture was subsequently hardened to require a responsive counter under a known retained allocation before using it. That fixture-only change does not change the installed DLL or retroactively validate the recorded zero values.

### Preparation and object loading

The initial native spawn marker reported 180.12 seconds, versus 35.14 seconds in the final session. Subsequent native-code inspection confirmed that this marker uses accumulated game `dt` in `m_respawnWait`, not whole-join monotonic wall time. Both reached the same 10,738 instantiated scene objects before the controlled workload. This is a promising observation, **not an established 80% loading improvement**, because the starts and background load were not controlled equally.

The new measurements confirm substantial leading preparation in busy intervals: examples contain about 5.6-6.0 ms preparation per batch before subsequent service. With the original 4 ms whole-batch clock, such preparation can leave only the existing one-success progress floor. The new option provides a creation allowance after that leading work, reducing the need to repeat preparation after very little progress. It can lengthen individual whole batches; readiness, native order, success floor, count limits and shared distant/nested allowance remain. No scan/sort cache or asynchronous spawning is used. See [clock semantics and tests](budget-preparation.md).

### Diagnostics, size and overhead

All 34 registered `probe.*` capabilities were enabled in both actual Unity processes. Memory APIs returned available samples. Render timing correctly reported headless; GPU timing remains unvalidated in a rendered session.

The final client confirmed eight pickup inventory acceptances and one container GUI opening, with no action-probe failure. Request-to-acceptance pickup mean was about 1.32 ms; container opening was about 55.77 ms for one observation. These were local-owned test actions, not remote loot under contention. No ownership-request completion was observed, so that latency remains unavailable. Native false inventory results can represent partial transfers; ambiguous/censored observations are separately accounted for.

The two final JSONL captures totaled about 1.31 MB. At the configured three-second cadence, extrapolating average interval-record sizes gives about **56.6 MB/hour combined**; using each role's largest observed interval gives about **63.3 MB/hour combined**. Actual short-run throughput was lower because automatic collector backoff lengthened intervals. These are projections, not a hard rolling hourly byte cap or a measurement of other mods' verbose logs. File/directory limits remain enforced.

Complete collector polls after the first averaged about 0.47 ms client and 0.84 ms server in this contended run. First-poll peaks were 60.28 and 28.80 ms. Background writes sometimes waited much longer without blocking the collector. These measurements include cold-start/OS scheduling effects and cover collection, not all Harmony or timing-hook overhead. They do not establish that logging is free. Both writers completed, with zero dropped records and zero recorded probe failures.

### Multithreading outcome

The [bounded-worker research](cpu-memory-parallelism-2026-09-15.md) completed 224 verified desktop batches. Parallel independent compression can improve throughput, but may increase CPU consumed, queued-item waiting, copied inputs and thread stacks. A live synchronous save cannot become nonblocking merely by dispatching compression and immediately waiting for it.

No additional simulation/save worker pool is shipped. Native world saving and our JSONL export already use background workers. Live AI, shared path state, ZDO snapshot mutation and collider readiness still require explicit immutable-input and commit contracts. These remain documented research gates rather than enabled unsafe patches.

### Verification and deployment identity

- Core: 37/37 tests. Python reports: 33/33 tests.
- Actual client and server assemblies: existing 496 checks and AI contracts, 173 map-bit checks, 209 package checks, 230 cache checks and 37 preparation-clock checks per installation.
- Action contracts: 37 metadata checks per installation on a modern CLR; standalone .NET Framework reports its native-interface limitation explicitly. Actual Unity exercised the installed action hooks.
- Both sessions verified 168 existing save files unchanged. Only four automatic Unity session bookkeeping registry values changed during startup; targeted restoration reproduced the full original preference export.
- All test processes stopped. No QA DLL active in either normal installation.
- Installed version: **0.4.0.0**, 151,552-byte DLL on both client and server.
- SHA256: `3FD81E8DED2CAE25B9B1C7D00936B3E1D6D94043F9D69581D6FFD3EC7127A656`.
- Prior 0.3.3 DLL/config backups and deployment receipt are retained privately under `.qa/deployment-backups/20260915T120535Z-0.4.0` and `.qa/deployment-0.4.0.json`.

Private evidence: initial run `.qa/runs/20260915T113624Z-4a308c`, final run `.qa/runs/20260915T115737Z-85e56d`, corresponding JSON trials/captures/observer files, and `.qa/frontier-v040-{client,server}-final.md`. Game assemblies, saves and raw captures are not included in public source.
