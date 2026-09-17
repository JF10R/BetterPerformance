# Updating the plugin after a Valheim update

Every probe and optimization in this plugin binds to native methods of the installed `assembly_valheim.dll`, `assembly_utils.dll` and `Splatform.Steam.dll` by exact signature, and the optimizations additionally validate the IL shape they patch. A game update can rename a method, change a parameter, reorder IL or change a texture format. The plugin is built so that a broken contract disables that one module and reports it instead of crashing, but "disabled silently in a capture" is not "working". This guide is the procedure to bring the plugin back to a verified state.

### 1. Detect the change

- Steam shows the new build; `docs/validation-*.md` record the last verified game version and the `assembly_valheim.dll` SHA-256 (see `docs/cpu-memory-parallelism-2026-09-15.md` for the 1.0.12 hash).
- In a capture, the start record carries `game_version` and every `probe.*` label; interval records carry each module's `*_status` label. Any value other than `enabled` or `installed` after an update is a contract that needs attention: `unavailable`, `unsupported_layout`, `type_unavailable`, `patch_failed`, `unpatch_failed:*`.
- BepInEx `LogOutput.log` prints one line per module at startup with the same status.

### 2. Run the one-command check

```powershell
pwsh -File scripts/check-game-update.ps1 -ValheimDir 'D:/Steam/steamapps/common/Valheim' -ServerDir 'D:/Steam/steamapps/common/Valheim dedicated server'
```

It rebuilds the plugin against the installed assemblies with warnings as errors, runs the offline C# suite and the Python report suite, then runs the game-contract harness (`tests/BetterPerformance.GameTests`) against the client and the dedicated server. The harness verifies, by reflection and by reading IL through Mono.Cecil, every hooked signature, every transpiler contract and the rejection of malformed layouts. It launches nothing. Logs land in `.qa/game-update-check-*.log`.

Read the output in this order:

1. Build errors: a removed or renamed type or member. The compiler names it; fix the reference and re-check the decompiled method before trusting the fix.
2. Harness failures: every module runs even when an earlier one fails, each failure prints as `FAIL <module>: <expectation>`, and the run ends with one `FAILED game contracts (n): …` line and exit 1. Read the whole list, not the first entry. Before 2026-09-17 the first failure threw and silently skipped every module after it, which hid a second drifted contract for an unknown number of runs.
3. Contract warnings the harness prints while still passing: modules that report `unavailable`/`unsupported_layout` against the real assembly. `STATIC ONLY` and `type_unavailable` on the standalone CLR are expected for Unity and Steam interface types and are not update failures.

### 3. Fix a broken contract

For each affected module, in this order:

- Decompile the new method with ILSpy (`ilspycmd -p -o <scratch outside the repo> assembly_valheim.dll`; never commit decompiled sources) and compare with the previous shape described in the module's doc (`docs/*.md` for each module lists the invariants and the IL it relies on).
- If the change is cosmetic (a renamed local, an added unrelated call), extend the contract check to accept both shapes and keep the same equivalence argument.
- If the change is semantic (different loop bounds, a new field written, a different serialization order), do not widen the check. Disable the module by default for that game version, update its doc, and re-derive the optimization from the new code before re-enabling.
- Timing probes (`TimingHooks.cs`): adjust the parameter type list; a probe reports `unavailable` rather than failing, so check the harness `probe.*` list rather than assuming.
- Re-run step 2 until both harness runs exit 0 with no unexpected contract warnings.

### 4. Validate at runtime on a disposable world

Never validate on a real character or world. The isolated runner copies the game into `.qa/runs/<id>/`, generates a `bp_test_` world, uses a temporary character, verifies by hash that every real save file is unchanged, and restores preferences:

```powershell
pwsh -File .qa/run-v045.ps1 -MaxRuns 1 -TestVariant fast_join_baseline
```

Then read the captures with `scripts/summarize_capture.py` and confirm: `probe_failures_total` = 0, `writer_dropped_records_total` = 0, every module status `installed`/`enabled`, and the module-specific counters listed in the latest `docs/validation-*.md`. Modules that the headless workload cannot exercise (cloud writes, world-map generation, terrain operations, second-player ownership) keep their "unproven at runtime" note and are watched in the first real session through their `*_status` and `*_result` labels.

### 5. Deploy and record

- Copy `.qa/deploy-0.4.5.ps1` to a new version, keep its checks (no game running, version match, validated-run markers, DLL hash equality with the validated run, no QA DLL in `plugins`, backup before copy, restore on failure) and run it with the validated run root.
- Bump `PluginVersion` and the csproj `<Version>`, add the CHANGELOG entry, and write `docs/validation-<version>.md` with the game version, assembly hash, gate results and what stayed unproven.
- Rollback: the deployment backup directory under `.qa/deployment-backups/` holds the previous DLL and config for each role.

### 6. Known update-sensitive points

| Module | What breaks it | Where the contract lives |
| --- | --- | --- |
| Timing probes | Signature changes on ~90 hooked methods | `TimingHooks.cs`, `AiTelemetry.cs` |
| Object creation budget, initial loading | `ZNetScene.CreateObjectsSorted` / `ZoneSystem.CreateLocalZones` IL | `ObjectCreationBudget.cs`, `InitialLoadingOptimization.cs` |
| Map serialization, exact map cache | `Minimap.GetMapData`, `ZPackage.WriteCompressed`, `BitArray` fields | `FastMapSerialization.cs`, `MapCompressionCache.cs` |
| Package copy | `ZPackage.Write(ZPackage)` and the `SendZDOs` call | `PackageCopyOptimization.cs` |
| Cloud write buffer | `SteamCloud.WriteFile` chunk loop and the 100 MiB constant | `CloudWriteOptimization.cs` |
| Minimap texture cache | `Minimap.GenerateWorldMap` IL closure and texture formats | `MinimapTextureCache.cs` |
| Replication cadence | `ZDOMan.CreateSyncList`, `ZDOPeer`/`PeerZDOInfo` fields, `ZSyncTransform.OwnerSync` | `ReplicationCadence.cs`, `ReplicationTelemetry.cs` |
| Owner-grant expedite | `ZDOMan.RPC_ZDOData` apply sites, `ZDO.SetOwnerInternal` | `OwnershipExpedite.cs` |
| Terrain coalescing | `TerrainComp.PaintCleared` local functions and `Save` gate | `TerrainSaveCoalescing.cs` |
| Attribution | `ZRoutedRpc.HandleRoutedRPC`, `RoutedRPCData` fields, `ZDO.Serialize` | `AttributionTelemetry.cs` |
| Loading details | Which subpaths `AltBiomeWorldData.VerifyBiomeData` calls | `LoadingDetailsTelemetry.cs` |
| Loot visibility | `MineRock5.RPC_SetAreaHealth`, the private `ZDOMan.CreateNewZDO(ZDOID, Vector3, int)` and its zero-hash arrival call site | `LootVisibilityTelemetry.cs` |

A pinned raw-IL hash is the most update-fragile contract in the repo: the bytes include
metadata tokens, which renumber whenever anything else in the assembly changes. An unchanged
body size beside a changed hash points at token churn rather than a logic change — evidence
worth stating, never proof on its own. Confirm against the decompiled body and the module's
documented invariants before re-pinning.

A green harness proves the contracts still hold on the installed build. It does not prove performance; that needs the isolated session and then a real session with the same A/B discipline as before.
