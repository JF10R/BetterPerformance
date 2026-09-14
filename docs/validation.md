# Validation record

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

### Known measurement limitation

`process_working_set` returned zero throughout both runtime captures. Treat this field as unavailable on this tested Unity/Mono runtime, not as zero memory consumption. Its collection needs correction and runtime verification before memory analysis. Other process fields require independent validation before using them for optimization decisions.

### Not yet validated

- Rendered gameplay, GPU load, remote clients and general mod compatibility.
- Total collection overhead, long-session behavior and effects on frame/update timing. Self-timed collector sections do not measure the complete instrumentation cost.
- Controlled comparisons of draw distances, networking changes or other optimizations.
- Whether any measured bottleneck explains a particular player's delayed actions.

No runtime performance gain is claimed.
