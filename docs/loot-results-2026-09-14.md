# Loot scheduling measurements — 2026-09-14

### Finding

In one balanced, continuous headless session, increasing the creation quota under the existing 4 ms budget reduced observed client loot availability latency. Budget plus quota averaged **158.73 ms**, compared with **268.57 ms** with all three options disabled (40.9% lower). Adding nearby-loot priority averaged 143.45 ms, but its incremental effect varied in direction across blocks. No rendered-FPS or general server performance improvement is established.

### Design and validation

- BetterPerformance 0.3.0, source commit `77822bd`; Valheim 1.0.12, ValheimPlus 0.10.1.2, BetterNetworking 2.3.3, PlantEverything 1.21.2 and PlantEasily 2.2.0. BetterNetworking remained enabled in every window. This is a new reference, not a comparison against the older ValheimPlus 0.10.1.1 session.
- One isolated dedicated-server/client pair, cloned QA world and temporary `perf01` character. Hidden `-batchmode -nographics -nosound`, client audio verified zero, Ultra simulation radius 5 plus far extension 2, explicit 30 Hz pacing, two Unity workers and BelowNormal process priority.
- Sixteen 45-second windows, four per configuration. Order: `0,1,3,2 / 1,2,0,3 / 2,3,1,0 / 3,0,2,1`. Every configuration appears once per block and twice at each of two alternating locations. The reference keeps diagnostics and installed hooks present but disables their behavior.
- Each window teleports, waits 15 seconds, creates 64 server-originated Wood drops at individually measured terrain height +2 m, observes availability, monitors position/presence for six seconds, and removes exact test network IDs through native destruction.
- The harness explicitly switches the options in both processes. Accepted telemetry verifies budget/quota/priority enablement against the window label. Budget is 4 ms, expanded allowance 64, loot radius 20 m.
- 17 C# and 11 Python offline tests pass, including new quota and priority regression fixtures. Export-to-report integration passes. Release builds against client/server references pass without warnings. Local IL/state verification passes 211 assertions against each installed game assembly; these include instruction checks, not 211 gameplay scenarios.
- Both captures completed: 787 client and 827 server records, zero drops and zero recorded probe failures. All 1,024 loot IDs appeared and were removed. The 26 protected real-world save files retained their hashes and count after the primary session.

The same client-built DLL ran on both roles: SHA-256 `2B18D71C686421EA0C6E21249C30B5EB6486A8A716711F1C434586E183A416C5`. Primary local run: `.qa/runs/20260914T164722Z-3d4df7`, with protocol, binary hashes, captures, trial records and `loot-analysis.json`. Raw captures and game assets remain local.

### Client results

| Configuration | Mean loot availability | Mean window loot p95 | Mean window creation-batch peak | Mean loop interval | Pauses >50 ms |
|---|---:|---:|---:|---:|---:|
| 0: options disabled | 268.57 ms | 483.78 ms | 26.96 ms | 33.296 ms | 12 |
| 1: budget only | 257.52 ms | 457.38 ms | 13.83 ms | 33.193 ms | 7 |
| 2: budget + quota | 158.73 ms | 222.73 ms | 14.80 ms | 33.226 ms | 9 |
| 3: budget + quota + priority | 143.45 ms | 199.14 ms | 14.63 ms | 33.215 ms | 8 |

Each row contains approximately three minutes of workload and 256 item observations. The p95 column averages the four window percentiles; it is not a pooled percentile. Creation peaks average the four per-window maxima. Including phase-boundary intervals leaves these client creation-peak values unchanged. Full independent loop samples retain boundary/teleport pauses.

The budget still reduces creation spikes relative to disabled behavior. Adding quota retains much of that improvement but does not establish a lower total CPU cost: mean client CPU was 258.93, 244.59, 263.83 and 266.43 CPU-ms/s respectively. Mean loop p95 was 34.052, 34.069, 34.170 and 34.162 ms. These small loop differences do not establish smoother rendered gameplay.

### Uncertainty

Candidate minus reference, comparing window means within each four-window block:

| Contrast | Mean loot change | Range across four block differences |
|---|---:|---:|
| Budget minus disabled | -11.05 ms | -31.26 to +25.63 ms |
| Add quota to budget | **-98.79 ms** | **-110.18 to -86.03 ms** |
| Add priority to budget + quota | -15.28 ms | -62.47 to +16.19 ms |
| Budget + quota minus disabled | -109.84 ms | -133.09 to -60.40 ms |
| All options minus disabled | -125.12 ms | -175.56 to -63.11 ms |

These are descriptive ranges, not confidence intervals. There are four repetitions per configuration in **one correlated session**, not 256 independent trials per configuration. Location is balanced overall but differs within some block comparisons. Cold traversal, cache state, temporal drift, normal background activity and brief QA preparation/build activity on the same host can affect results. No statistical significance or universal percentage gain is claimed.

Availability uses Windows QueryPerformanceCounter shared by the two local processes. The timestamp starts after server instantiation returns; the client first checks after a file acknowledgment/identity manifest, then at approximately 30 Hz. Thus it includes observation delay and excludes the instantiation call itself. Receive-to-visible observations can miss earlier events. This is neither exact networking RTT nor player action-to-loot latency, and does not represent the second PC's Wi-Fi connection.

### Server and scheduling evidence

The dedicated server recorded zero budget exits, creation attempts and quota expansions during retained measurement intervals. It was not limited by scene-object creation in this workload; no direct server gain from these switches is demonstrated. Mean server loop intervals were 33.443, 33.344, 33.359 and 33.341 ms.

On the client, quota configurations recorded 5,412 and 5,338 expansion calls. The combined configuration recorded 12 priority passes selecting 490 candidates, 53 vanilla-order passes and no bounded fallback. These are counter differences between first and last retained samples in each window; they omit boundary work and are not exact whole-window event totals or distinct item counts. Priority running does not itself prove that candidate order changed. The primary workload mostly contains identical loot after terrain has settled.

### Safety scope

The primary session observed no spontaneously missing test IDs, no missing terrain-height samples and no sampled item position more than 0.5 m below terrain. Minimum observed clearance was -0.113 m. Some drops remained near their initial height on the client, so this alone does not establish active gravity/collision coverage. Native non-owner synchronization can disable client gravity; additionally, the low-elevation location has terrain approximately 11–14 m high while the player is around 28–29 m, and is not a dry-ground falling scenario.

### Supplemental competition and physics check

A separate four-window session completed at `.qa/runs/20260914T170152Z-dd60cd`, using the same plugin binary and QA fixture. Modes ran once each in order `0,1,2,3`; this is a functional check, not a balanced performance comparison. Additional observation hooks can themselves add cost, especially in the priority mode. Its latency figures do not contribute to the main estimates above.

Each window created 64 nearer `Pickable_Branch` objects before 64 Wood drops. The harness verified that both prefab types use the vanilla Default tier and that the competing prefab has no root ItemDrop. After availability observations, only the tracked Wood objects claimed native client ownership, waited two frames, were repositioned at terrain +2 m, and had their rigidbodies awakened. Position, ownership and gravity were then observed for six seconds. A local QA hook also recorded native TerrainCheck upward corrections for those exact items during this observation period.

| Functional observation | Result |
|---|---:|
| Wood IDs appeared, remained present, then removed | 256 / 256 |
| Competing branch IDs present before cleanup | 256 / 256 |
| Tracked Wood or branch instances remaining after cleanup | 0 / 512 |
| Wood IDs observed with local ownership and enabled gravity | 256 / 256 |
| Wood IDs observed descending more than 0.5 m after repositioning | 98 / 256 |
| Sampled Wood IDs below terrain by more than 0.5 m | 0 |
| Client TerrainCheck corrections on tracked items during observation | 0 |
| Missing terrain-height samples | 0 |
| Priority-mode passes containing both prefab kinds | 4 |
| Priority-mode passes with actual candidate-order changes | 3 |

Downward movement was confirmed for 49 items in the disabled configuration and 49 with budget plus quota. The budget-only and combined-priority windows used the low-elevation location and did not demonstrate downward falls; they are **not** dry-ground collision validation. Ownership/gravity counters mean observed at least once, not continuously certified throughout the window. The priority hook observed 130 changed candidate positions across three passes, not 130 distinct objects or successful promotions. All competing objects were eventually present, but this small finite workload does not establish starvation freedom.

Supplemental captures completed with 228 client and 259 server records, zero drops and zero recorded probe failures. Both runtime pairs stopped cleanly. The protected world files still matched all 26 original hashes and the original count. Across both sessions, 1,280 tracked loot IDs appeared and were removed. Original worlds and characters were not used as test targets; no plugin was deployed to either original installation.

This testing does not establish collision readiness during terrain streaming, building-floor safety, general mod compatibility, or absence of brief penetration. Native `ItemDrop.TerrainCheck` can correct underground items outside the monitored period; native stacking can legitimately merge network IDs when its conditions are met. A missing ID alone is not proof of lost item quantity.

### Interpretation

Budget plus quota is the best-supported addition for lower observed loot latency in this workload. Nearby-loot priority remains experimental because its incremental result is mixed and workload-dependent. Production defaults remain disabled; no existing installation, world or character is a deployment target of these tests. The previous 0.2.0 package is not replaced by this measurement work.
