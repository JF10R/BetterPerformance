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

### Not yet validated

- Loading the plugin and applying Harmony patches inside Valheim.
- Runtime coexistence with ValheimPlus, BetterNetworking and other mods.
- Collection overhead, long-session behavior, and effects on frame/update timing.
- Actual save, zone-loading and multiplayer captures.
- Whether any measured bottleneck explains a particular player's delayed actions.

No runtime performance gain is claimed. The development and validation described here did not launch Valheim or its server and did not deploy the plugin.
