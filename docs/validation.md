# Validation record

### Version 0.4.0 — 2026-09-15 UTC

The [0.4.0 runtime report](frontier-runtime-0.4.0.md) records the final installed DLL, configuration, paired-save measurements, package parity, preparation-clock behavior, resource/action observations and limitations. Core tests pass 37/37 and Python reports 33/33; actual client/server game-contract checks and two disposable Unity sessions completed. Both normal installations received the same verified DLL, with no active QA DLL. Wardogs ran concurrently; timing uncertainty and the invalid optional Mono allocation counter are explicitly documented. Worker architectures remain research prototypes, not shipped simulation changes.

### Version 0.3.3 — 2026-09-15 UTC

The [isolated runtime report](frontier-runtime-2026-09-15.md) supersedes earlier pending-validation notes for configuration history, budget observations, save/RPC and AI probes. It includes 34 core test groups, 22 Python tests, both reference builds, current game contract checks, actual Unity probe installation, map payload parity, temporary-character reload and paired local-save timings. The tested DLL was installed on both usual local installations; no QA DLL was deployed. This does not establish normal-session gains or every save/mod combination.

### Version 0.3.2 — 2026-09-14 UTC

- 27 C# test entries and 19 Python tests pass, including retained game timing/failure accounting, recorder sampling across drains, zero hot-path managed allocations after warmup, bounded poll backoff/recovery and metadata-aware coverage reports.
- Client/server Release builds pass without warnings. The client-built DLL passes the existing 447 IL/state assertions against each local game assembly; no game process launched.
- Seven paired offline x64 hot-path measurements per runtime show `MetricBook.Record` decreasing from 76.076 to 18.126 ns in .NET Framework and 64.047 to 14.393 ns in .NET 10. This isolates aggregation, not complete Harmony/Unity/logging cost. See [method and limits](logging-overhead-2026-09-14.md).
- [Real-session safeguards](play-session.md) reduce optional collection frequency after overruns and label reduced coverage. No claim of negligible runtime overhead or measured gameplay gain.

### Version 0.3.1 — 2026-09-14 UTC

- 25 C# test entries pass, including five loot-tracker scenarios under one entry, storage allowance, linked segment export and five slow-summary checks. 17 Python tests pass, covering legacy captures, segmented reports, alert caps and loot coverage.
- Release builds against current client and dedicated-server references pass without warnings. The client-built single DLL passes 447 IL/state assertions against each installed assembly, including passive observation both alone and following the budget transpiler, instruction/label/exception-block preservation and unsupported-layout rejection. These are not gameplay scenarios.
- New continuous capture and telemetry are validated offline only. No client/server process launched for preparation; no fresh runtime overhead, achievement or long-session certification is claimed.
- [Real-session configuration and limits](play-session.md) distinguish schema-based log-size projections from runtime measurements. The hard file cap may omit final interval counters; normal duration rollover preserves them.

### Version 0.3.0 — 2026-09-14 UTC

- Add separately disabled adaptive-quota and nearby-loot scheduling options, bounded classification caches, regular vanilla-order passes and diagnostics.
- Following renewed test authorization, 17 C# and 11 Python tests pass, including quota, tier-order, rollback and cadence fixtures. Export-to-report integration passes. Local Release builds against client/server references pass with zero warnings.
- The game verifier passes 211 assertions per installed assembly, including original instruction/exception-block preservation, unsupported-layout rejection and active/disabled quota behavior. These are static/state checks, not 211 gameplay scenarios.
- A continuous sixteen-window isolated headless comparison completed with four repetitions per configuration. All 1,024 tracked loot IDs appeared and were removed; both captures completed without dropped records or probe failures. Budget plus quota reduced mean observed loot availability from 268.57 to 158.73 ms. The incremental priority effect is mixed across blocks; no rendered-FPS or direct server gain is established.
- A separate four-window functional check completed with 256 loot and 256 competing nonloot objects. It observed actual priority reordering, native ownership/gravity, 98 downward falls and no sampled deep penetration or native terrain correction during monitoring. Low-elevation windows did not demonstrate dry-ground falling. Both runtime pairs stopped; protected world hashes and count remained unchanged.
- See [measurements and uncertainty](loot-results-2026-09-14.md) and [behavior and remaining limits](loot-latency.md). The original implementation commit intentionally skipped CI when testing was deferred. The tested 0.2.0 package is not replaced.

### Version 0.2.0 — 2026-09-14 UTC

- Thirteen C# and eleven Python tests pass. Local Release builds pass against client and dedicated-server references.
- The optional local game verifier passes 203 assertions per installed assembly, including default-off behavior, original IL/exception-block preservation, unsupported-layout rejection and nested state cleanup. These include per-instruction checks, not 203 independent scenarios.
- One continuous eight-window headless comparison completed at Ultra with BetterNetworking 2.3.3 and ValheimPlus 0.10.1.1 present. All 512 tracked test items appeared and were removed; both captures completed without dropped records or probe failures.
- Client creation-batch peaks decreased, while overall loop gains were modest and loot latency varied across pairs. The server did not exhaust its budget. The optimization remains disabled by default; see [results and uncertainty](object-budget.md).
- Test processes stopped. The 26 protected real-world save files retained their hashes and count. Existing installations and characters were not test targets.

### Version 0.1.1 — 2026-09-14 UTC

- Eleven C# tests pass, including native resident-memory availability, marker validation and phase-boundary identification.
- Eleven Python tests pass, including two-/three-pair uncertainty, incomplete/duplicate comparison rejection, strict stall thresholds and invalid-memory reporting.
- Release compilation succeeds against local Valheim 1.0.12 client and dedicated-server references with zero compiler warnings/errors.
- Initial isolated runtime verification confirms native memory readings and native Steam telemetry on both roles, including the dedicated Steam interface and ServerSync buffering wrappers.
- Added probe timings, GC-correlated loop gaps and bounded markers remain observational. No networking, spawning, save or simulation policy is changed.
- Six completed comparison runs and a separate ten-minute continuous workload use the same tested 0.1.1 binary. All 14 captures close without drops. [Results and uncertainty](runtime-results-2026-09-14.md) document the observed adverse overhead signal, limitations and excluded setup pilots.

### Version 0.1.0 — 2026-09-13

- Nine offline C# tests pass: histogram bounds and invalid samples, concurrent draining, JSON serialization under a non-English locale, bounded queue overflow, writer failure, combined overflow/failure accounting, file-size limits, protection against overwriting existing files, and monotonic clock conversion.
- Five Python report tests pass: aggregate interpretation, negative adjusted queues, explicit truncated-tail handling, rejection of interior corruption, dropped-record warnings, schema checks and capture-ID consistency. Some tests cover multiple conditions.
- A synthetic capture written by the C# exporter is accepted by the Python report command.
- Release compilation succeeds against local Valheim 1.0.12 client and dedicated server assemblies, with zero compiler warnings/errors.
- Static inspection resolves all 44 plugin member references into the game, Unity, BepInEx and Harmony for both installations.
- All ten exact probe signatures and the scene instance dictionary field are present in both installations.
- The ZIP is inspected to contain only the BetterPerformance DLL, project documentation, the report script and project license. No proprietary assemblies or raw captures are included.

Static inspection reads assembly metadata without executing the game or the plugin. Game assemblies remain local and are not committed. Public CI runs only the game-independent tests on Windows and Linux.

### Isolated runtime test — 2026-09-14 UTC

- Launched fresh, isolated client/server runtimes with explicit user authorization, a random world and a temporary local character. Both processes completed and stopped. Existing world files retained their SHA-256 hashes.
- Valheim 1.0.12 loaded BetterPerformance 0.1.0 with all ten probes enabled, alongside ValheimPlus 0.10.1.1, BetterNetworking 2.3.3, PlantEverything 1.21.2 and PlantEasily 2.2.0. PlantEasily correctly skipped server patches.
- BetterNetworking negotiated compression. A disconnect warning about a nonexistent null peer occurred; this run does not establish its cause or general compatibility.
- Both processes ran in batch mode without graphics or visible windows. Client audio was verified at zero. Effective simulation radius was verified at Ultra (near 5, far extension 2), using a local test harness.
- Completed warmup, 32 loot spawns, removal of that loot, an asynchronous save request and traversal steps of 128 m and 256 m.
- Client capture: 229 accepted/written records plus footer over approximately 258 seconds. Server capture: 445 accepted/written records plus footer over approximately 458 seconds. Both finished with zero dropped records and zero recorded probe failures.
- Real object, zone, networking and save timings were exported and accepted by the report script. Raw captures, harness and game assets remain local.

This was a functional measurement test with explicit 30 Hz harness pacing, reduced process priority, two Unity worker threads per process and another game running. It was not a graphics benchmark or a controlled optimization comparison. A first attempt used Classic simulation radius and different pacing; it is not a valid baseline for the corrected Ultra run.

### Version 0.1.0 measurement limitation

`process_working_set` returned zero throughout both 0.1.0 runtime captures. Treat this field as unavailable in those captures, not as zero memory consumption. Version 0.1.1 uses Windows PSAPI and omits failed/zero readings. Other process fields require independent validation before using them for optimization decisions.

### Not yet validated

- Rendered gameplay, GPU load, remote clients and general mod compatibility.
- Total collection overhead, long-session behavior and effects on frame/update timing. Self-timed collector sections do not measure the complete instrumentation cost.
- Broader controlled comparisons of draw distances, networking changes or other optimizations. The two-pair headless comparison is exploratory and is not a low-overhead certification.
- Whether any measured bottleneck explains a particular player's delayed actions.

No runtime performance gain is claimed.
