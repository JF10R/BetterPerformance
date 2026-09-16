# Validation: 0.4.6

2026-09-15 (local). Diagnostics-only release: 52 gameplay-loop timing probes and 44 gameplay counters, no new optimization switch. Built against Valheim 1.0.12 / Unity 6000.0.75f1.

### Offline gates

| Gate | Result |
| --- | ---: |
| Core/offline suite | 44/44 |
| Python report suite | 64 tests OK |
| Game-contract harness, client | exit 0 (497 gameplay-probe checks, 264 gameplay-counter checks, all earlier suites) |
| Game-contract harness, dedicated server | exit 0 |

### Isolated runtime session

Headless client plus dedicated server on the disposable `bp_test_` world with the temporary character, the 0.4.5 switches on. Run root `.qa/runs/20260916T033225Z-337bf2`; 180 real save files and the preferences registry verified unchanged.

| Observation | Client | Server |
| --- | ---: | ---: |
| Probe failures / dropped records | 0 / 0 | 0 / 0 |
| Probes not enabled | `SlowUpdateLoop` (coroutine) | same |
| Gameplay probes unavailable | none | none |
| Timing metrics with calls in the run | 72 | 55 |
| Median interval record size | 56 KB | 47 KB |

Idle timing summaries are now omitted from interval records, so the record size stayed at the 0.4.4 level despite 52 added metrics. Per-frame probes observed at the expected rates (`InventoryGuiUpdate`, `HudUpdate`, `MinimapUpdate` once per frame; fixed batches at 50 Hz), with maxima under 5 ms. Counters that the headless workload cannot exercise (chest opens, smelters, building, gathering, combat, vehicles) stayed at zero without errors; `placement_ghost_updates`, `container_changes`, `minimap_fog_pixels_explored` and `zsfx_instances_max` did register.

### What the next real session should show

- Where the inventory and chest GUI, placement ghost and large map spend their frames, and whether any of them reaches the 20 ms slow-operation threshold.
- `container_concurrent_open_conflicts` on the host client: how often the second player hits an in-use chest, the demand figure for the shared-chest feature.
- Smelter catch-up sizes on return to base, tree/rock damage counts against the per-target `RPC_Damage` attribution, and ship/cart frames.
