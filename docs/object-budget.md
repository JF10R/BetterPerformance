# Experimental object-creation budget

This page records the **0.2.0 budget-only behavior and experiment**. Version 0.3.0 adds separately disabled [quota and loot-priority options](loot-latency.md); no tests have been run for those additions, and the results below do not validate them.

### Behavior

Version 0.2.0 adds one optimization module, disabled by default. It adds a soft elapsed-time budget to `ZNetScene.CreateObjects`, shared by the near and distant creation loops. The clock starts before vanilla candidate scanning and sorting. Between candidates, an exhausted budget ends the existing enumeration through its normal disposal path. Remaining objects stay pending for a later update.

The module retains vanilla priority/order, active-area and per-zone readiness, object-count limits, ownership, invalid-prefab cleanup, and creation/destruction code. It does not prioritize loot specially, skip collision requirements, change simulation distance, reduce replication fidelity, modify save formats, or copy BetterNetworking code.

At least one **successful** creation is allowed per batch that has a creatable candidate. Failed prefabs do not consume that progress allowance: otherwise a retained invalid prefab on a client could indefinitely block valid objects behind it. This regression has an offline test.

This is not a hard frame-time cap. Scanning, sorting, readiness work, invalid-prefab attempts and a single expensive object can exceed the budget. Near work may consume the shared allowance before distant objects receive time. Loading, teleport completion and item availability can become slower. Those tradeoffs require measurement.

### Configuration

In `BepInEx/config/jf10r.BetterPerformance.cfg`:

```ini
[ObjectLoading]
Enabled = false
BudgetMilliseconds = 4
```

Set `Enabled = true` and restart the local game/server to install the experimental patches. Supported budget values are 1–20 milliseconds. Diagnostics can be disabled independently with `Capture.Enabled = false`.

After installation, use these local console commands:

```text
bp_budget status
bp_budget off
bp_budget on
```

Runtime switches do not persist configuration or change another process. A client and server can each use their own setting; the module introduces no network protocol and requires no matching installation on peers. Compatibility with other mods modifying the same creation loops remains to be established beyond the tested set.

Main-thread automation may call `Plugin.SetObjectCreationBudgetEnabled(bool)`. It returns false if the module was not installed at startup or the call is on another thread. Unknown method signatures or unsupported enumeration layouts cause installation to roll back its own patches and retain vanilla behavior.

### Measurement

Captures report actual runtime enablement, patch installation status, configured budget, and cumulative budgeted batch, creation-attempt and early-exit counters. Counter differences give interval activity; these are not queue lengths or deferred-object counts. Capture headers distinguish diagnostics-only mode from an installed optional optimization module.

The continuous experiment uses one isolated client/server pair at effective Ultra radius 5, with identical diagnostics and BetterNetworking 2.3.3 active throughout. Both processes switch together in eight 45-second workload windows: off/on/on/off/on/off/off/on. Windows alternate the same two locations, create 64 server-owned Wood items near the client, verify their client-side item/view presence, then verify despawn.

An independent observer measures loop gaps. Server creation timestamps and client observations use Windows `QueryPerformanceCounter`, a common native monotonic clock. The Unity Mono `Stopwatch` values observed in a pilot had different process origins and were unsuitable for this cross-process subtraction. A local test manifest communicates object IDs after the spawn burst; this adds observation delay and may hide short receive-to-create delays. Spawn-to-visible results are test-observed availability, not network RTT or player action latency. The server's direct spawn burst itself is outside the scene-creation budget; replicated client creation is inside it.

The native clock choice follows [Microsoft's guidance for timestamps on the same machine](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).

The eight windows share caches, world state and process lifetime. Their four adjacent on/off contrasts are descriptive, correlated comparisons—not independent repetitions. No confidence interval or significance claim should be inferred from their frame count. Initial loading/order effects and this synthetic scene limit generalization to a developed world or a Wi-Fi client. The previous 0.1.0/0.1.1 runs also had Wardogs running; their values are historical context, not the control for this experiment.

### Results

One continuous session completed on Valheim 1.0.12: eight 45-second windows, about six measured minutes, at a 4 ms budget. Client and server ran headless with explicit 30 Hz pacing, two Unity workers and below-normal process priority. Wardogs was closed. Light monitoring remained an uncontrolled source of machine load. This measures CPU/loading behavior, not rendered FPS or GPU performance.

| Client measure | Budget off | Budget on | Interpretation |
| --- | ---: | ---: | --- |
| Mean of each window's maximum object-creation batch | 25.29 ms | 12.92 ms | About 49% lower in this workload |
| Largest object-creation batch across windows | 31.39 ms | 15.59 ms | The 4 ms target is explicitly soft |
| Loop mean, averaged across windows | 33.201 ms | 33.174 ms | Almost unchanged at the pacing floor |
| Loop p95, averaged across windows | 34.178 ms | 34.148 ms | Almost unchanged |
| Loop pauses over 50 ms | 7 | 5 | About three minutes per mode; few events |
| Mean of each window's maximum loop gap | 127.55 ms | 110.49 ms | Some paired windows worsened |
| Mean observed spawn-to-client-item delay | 265.05 ms | 267.69 ms | Small average difference, variable pairs |
| Mean of each window's loot-delay p95 | 440.17 ms | 447.81 ms | Window p95 average, not a pooled percentile |

The four adjacent on-minus-off contrasts show the uncertainty:

- Mean loop gap: average **-0.026 ms**, observed pair range **-0.052 to -0.004 ms**.
- Maximum loop gap: average **-17.06 ms**, range **-55.34 to +17.54 ms**.
- Mean loot delay: average **+2.64 ms**, range **-53.07 to +69.59 ms**.
- Loot-delay p95: average **+7.64 ms**, range **-30.77 to +63.50 ms**.

These ranges describe the four observed contrasts; they are not confidence intervals or bounds on future behavior. The two destinations were balanced across modes, but cold loading, order and shared caches remain confounders. The first pair accounts for some of the favorable loop difference.

The client's early-exit counter increases totaled 414 between retained, correctly labelled samples with the budget on and zero with it off. This is not a complete event count across omitted boundary intervals. End-of-window scene counts were approximately 10,084–10,095 at one destination and 10,754–10,756 at the other across modes. All **512/512** explicitly tracked items appeared with valid item/view components and then disappeared following native network destruction. Cleanup targets exact test ZDO IDs because the dedicated server can unload their original GameObjects; it claims those test IDs before requesting destruction. No disappearance of the 512 tracked items was left unresolved.

Secondary method/CPU summaries exclude aggregation intervals tagged as crossing a phase boundary, intervals whose actual toggle label differs from the phase, and the shutdown tail after the client workload ends. Including boundary intervals leaves the reported largest object-creation batches unchanged. Observed client process CPU was about 209 ms/s off versus 207 ms/s on, which does not establish a meaningful CPU reduction.

The server never exhausted this budget in the measured windows: **zero early exits**, with creation batches below 0.1 ms. Its loop p95 stayed around 34.5 ms. This experiment does **not** demonstrate a server-side benefit; the useful binding limit here was on replicated client object creation. Both roles were toggled together, so their indirect effects are not separately identified.

**Decision:** retain the module as an opt-in experimental feature. It demonstrably spreads client creation work in this synthetic session, but a large general improvement in multiplayer smoothness, lower CPU use, unchanged action latency and behavior in a developed world are not established. Keep diagnostics and BetterNetworking separate; this change does not tune networking or saves.

Accepted local run: `20260914T022236Z-0d0c69`. Tested DLL SHA256: `D661B8BE76C9C0F01D4550AB3A816A9A3C5EE281CF10801BF9849E2EAE3789EF`. Captures completed with 409 client and 441 server written records, zero dropped records and zero probe failures. Both test processes stopped. The 26 protected real-world save files retained their original hashes and file count; the existing game installations and characters were not used as test targets.

Three incomplete pilots are excluded: `20260914T021143Z-834810` and `20260914T021511Z-45ec6b` exposed incomplete QA cleanup through unloaded/unowned GameObjects; `20260914T021846Z-8a6551` exposed the cross-process Mono clock offset. Only the isolated harness changed between these pilots and the accepted run; the tested plugin binary stayed the same. No raw saves, identities or captures are published.

### Verification

The game-independent suite covers the exact deadline, minimum progress, invalid-prefab fairness and fresh batches. An additional Windows/.NET Framework verifier reads locally installed game assemblies without starting Unity:

```powershell
dotnet run --project tests/BetterPerformance.GameTests -c Release '-p:ValheimDir=D:/Steam/steamapps/common/Valheim' -- 'D:/Steam/steamapps/common/Valheim' 'D:/GitHub/BetterPerformance/src/BetterPerformance/bin/Release/net472/BetterPerformance.dll'
```

Substitute the dedicated-server installation for both game paths to verify that assembly too. No game assemblies or decompiled bodies are included in the repository. The verifier checks default-off installation, both real creation methods, hook placement, original instruction/label/exception-block preservation, rejected unsupported branches and direct nested/finalizer state restoration. Its 203 assertions per installed assembly include individual instruction checks; they are not 203 independent scenarios and do not substitute for executing patched Unity methods.
