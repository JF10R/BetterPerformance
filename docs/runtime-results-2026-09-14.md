# Runtime measurement results — 2026-09-14 UTC

BetterPerformance 0.1.1 improves measurement coverage. These tests do not demonstrate a gameplay performance improvement; they show a possible instrumentation cost that needs further investigation.

### Implemented and tested

- Windows native resident-memory sampling replaces unusable zero values from Unity Mono.
- Native Steam pending/unacknowledged bytes, estimated rates, ping and queue time are separate from BetterNetworking-adjusted socket results. Both client and dedicated-server interfaces are supported, including known ServerSync buffering wrappers.
- Additional timings separate peer/RPC processing, save updates, sorted object creation and distant object creation.
- Bounded markers identify scenarios. Loop gaps spanning markers or GC collections are labeled without claiming those gaps are pure GC time or belong entirely to one phase.
- Reports include data validity, repeated-run ranges and explicit uncertainty.

No bandwidth, compression, replication, object-creation budget, save policy or simulation policy was changed. BetterNetworking 2.3.3 stayed active in every condition; its benefit over vanilla networking was not tested and its code was not incorporated.

### Comparison

Six isolated client/server runs used identical copies of one temporary-world fixture and character. There were two complete blocks, with three conditions per block: 0.1.0 at Ultra, 0.1.1 at Ultra, and 0.1.1 at Classic. A planned third block was canceled at the user's request in favor of a prolonged session. Setup/debug pilots were excluded.

Each run included approximately 65 seconds of loot spawning/removal, a save request and two traversals after loading and warmup. Both processes used headless mode without graphics, explicit 30 Hz pacing, BelowNormal priority and two Unity worker threads. Another game and diagnostic tools were active on the same computer. This workload does not represent rendered gameplay, a developed production world or a remote LAN client.

The unchanged independent QA observer supplied the comparison metrics. All 12 captures closed with zero dropped records, 1,491 accepted/written records in total, all probes enabled and no recorded probe failures. Plugin hashes were identical within each arm; effective configurations and observer binaries matched. Requested simulation radii were verified. The 26 protected production-world files retained their hashes.

| Client metric | 0.1.0 Ultra | 0.1.1 Ultra | 0.1.1 Classic |
| --- | ---: | ---: | ---: |
| Mean loop gap | 33.532 ms | 34.266 ms | 33.731 ms |
| Mean of run p95 gaps | 34.202 ms | 35.898 ms | 34.624 ms |
| Mean of run maximum gaps | 280.215 ms | 315.136 ms | 228.956 ms |
| Gaps over 50 ms per minute | 10.590 | 33.595 | 15.653 |

For 0.1.1 minus 0.1.0 at Ultra, the observed mean client loop-gap difference was **+0.735 ms**, with an exploratory 95% paired interval of **[-2.495, +3.965] ms**. The observed stall-rate difference was **+23.005 per minute**, with interval **[-35.228, +81.238]**. Both individual pairs were worse with the added diagnostics. The small sample and uncontrolled external load prevent reliable attribution or an equivalence claim; the adverse signal must not be presented as negligible overhead.

Classic minus Ultra under 0.1.1 showed **-17.943 client stalls per minute**, with interval **[-105.451, +69.565]**. Classic reduced observed scene occupancy from roughly 12,400 to 5,100 objects in this fixture. That changes simulation scope and is not a behavior-preserving mod optimization. The intervals use only two paired observations (df=1), assume approximately normal independent differences, and are not corrected for multiple metrics. They are exploratory and very uncertain.

Server stall rates averaged 20.702, 33.574 and 9.662 per minute respectively. Some secondary intervals exclude zero, but two pairs under uncontrolled external load are insufficient for broad performance claims. See the [complete generated comparison](comparison-2026-09-14.md) and [protocol](repeated-tests.md).

### What the additional probes reveal

Across the two new-Ultra workload captures, client sorted-object creation reached 72.221 ms and distant-object creation 71.175 ms. These are elapsed batch timings, not individual-object costs, and their maxima must not be added together. Client peer/RPC processing peaked around 26.6 ms; server peer/RPC processing around 23.2 ms.

The retained workload intervals captured server save preparation around 38 ms, save entry calls around 51 ms and asynchronous workers around 261–264 ms. Worker elapsed time is not an equivalent main-thread freeze or pure disk duration. These measurements strengthen the case for investigating object loading and time-bounded creation work while preserving readiness and object priority; they do not validate an optimization yet.

### Prolonged session

A separate continuous Ultra session completed **600.5 seconds of measured workload**, after loading and warmup, without restarting either process. It included the initial route, two additional loot/steady phases, a further 512 m traversal and a final stable phase. Both captures closed normally with zero drops: 638 client records and 692 server records, excluding completion footers. The same tested 0.1.1 DLL was used.

| Independent observer, whole ten-minute workload | Client | Server |
| --- | ---: | ---: |
| Mean loop gap | 33.430 ms | 33.698 ms |
| p95 loop gap | 34.627 ms | 35.928 ms |
| Maximum loop gap | 344.774 ms | 176.171 ms |
| Gaps over 50 ms per minute | 6.895 | 7.794 |

This session contains much more stable time than the short comparison workload. Its lower overall stall rate is **not** evidence of a performance improvement. It is one session (n=1), and its windows must not be counted as extra independent runs.

Within the plugin's separate callback measurements, traversal windows showed approximately 95–131 client gaps over 50 ms per minute, versus approximately 15–27 in stable windows. These phase summaries exclude aggregation intervals crossing markers. The plugin callback and the independent observer (which samples after the harness's pacing step) occupy different points in the Unity loop and can produce different distributions. Do not combine their counts or compare these phase rates directly to the whole-session observer table.

Resident memory stayed around 2,285–2,301 MiB on the client during the first two stable windows, then around 2,395–2,417 MiB after the additional traversal. The server stayed around 1,567–1,569 MiB. New terrain/object loading can increase retained memory; ten minutes does not establish or rule out a leak. Native Steam queue estimates in the retained phase windows peaked around 1.1 ms client and 10.3 ms server. One-second sampling may miss short spikes and excludes managed queues; it does not measure remote-player action latency.

Both processes stopped cleanly. The protected production-world files and the 15-file fixture snapshot retained their hashes. Gameplay preferences were sandboxed; only Unity session bookkeeping changed in the original registry. PlantEasily's server-only warning and BetterNetworking's null-peer disconnect warning remained; no BetterPerformance exception was found. No original game installation was updated.

### Reproducibility

- Valheim 1.0.12; ValheimPlus 0.10.1.1; BetterNetworking 2.3.3; PlantEverything 1.21.2; PlantEasily 2.2.0 (server patches skipped by that mod).
- Tested 0.1.0 DLL SHA-256: `cce353f8e7c15f4da6dd37a096573305bcdf8e1d8f048cfcd6e41ae9cb675747`.
- Tested 0.1.1 DLL SHA-256: `c85c1bd571143cd6bed95db20a0c0f32d72c561f6c2cd5d80eaf7fd966d2e4e4`.
- Raw captures, fixture saves, test harnesses and proprietary assemblies remain local. Only aggregate findings and original project code are published.
