# Validation record

### Version 0.3.0 — validation deferred

- Add separately disabled adaptive-quota and nearby-loot scheduling options, bounded classification caches, regular vanilla-order passes and diagnostics.
- Regression fixtures are added/updated but **not executed**, at the user's request. No game/server sessions, benchmarks or automated test suites were launched for this version.
- Source review and compilation are the only implementation checks; no new performance or runtime-compatibility claim. CI is intentionally skipped for the implementation commit.
- See [behavior, constraints and deferred checks](loot-latency.md). The tested 0.2.0 package is not replaced.

### Version 0.2.0 — 2026-09-14 UTC

- Thirteen C# and eleven Python tests pass. Local Release builds pass against client and dedicated-server references.
- The optional local game verifier passes 203 assertions per installed assembly, including default-off behavior, original IL/exception-block preservation, unsupported-layout rejection and nested state cleanup. These include per-instruction checks, not 203 independent scenarios.
- One continuous eight-window headless comparison completed at Ultra with BetterNetworking 2.3.3 and ValheimPlus 0.10.1.1 present. All 512 tracked test items appeared and were removed; both captures completed without dropped records or probe failures.
- Client creation-batch peaks decreased, while overall loop gains were modest and loot latency varied across pairs. The server did not exhaust its budget. The optimization remains disabled by default; see [results and uncertainty](object-budget.md).
- Test processes stopped. The 26 protected `sept_2026` files retained their hashes and count. Existing installations and characters were not test targets.

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
