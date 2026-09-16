# Diagnostics validation: 0.4.4

2026-09-15. Plugin DLL SHA256 `A25831DC9088E38D82CE50F283F90CE8F7A8013841CD00E6909227A7C2D0F07D`, built against Valheim 1.0.12 / Unity 6000.0.75f1. Diagnostics only: no gameplay, save, ownership or networking behavior was changed by this release.

### Offline gates

| Gate | Result |
| --- | ---: |
| Core/offline suite (`tests/BetterPerformance.Tests`) | 40/40 |
| Python report suite | 56 tests OK |
| Game-contract harness, client installation | exit 0; +141 base-simulation, +60 attribution, +221 ownership/host/network checks |
| Game-contract harness, dedicated server installation | exit 0, same checks |

The standalone harness verifies signatures and patch installation against the installed assemblies. It cannot type-load Unity interface types, so eight Heightmap/terrain/zone probes are verified only through the guarded installer path there.

### Isolated runtime session

One headless client plus dedicated server on the disposable `bp_test_` world with the temporary `perf01` character, BetterNetworking, ValheimPlus and the plant plugins present, at below-normal priority while another game was running on the host. Run root `.qa/runs/20260915T210648Z-73682d`; 168 real save files and the preferences registry verified unchanged afterwards.

| Observation | Client | Server |
| --- | ---: | ---: |
| Probe failures / dropped records | 0 / 0 | 0 / 0 |
| Probes not enabled | `SlowUpdateLoop` (coroutine) | same |
| Collector poll cost after the first poll | 0.46 to 1.14 ms | 0.45 to 2.96 ms |
| First poll (cold start, recorder scan) | 47.5 ms | 42.6 ms |
| Engine markers available / scanned | 6 / 44 | 6 / 44 |
| Steam transport path | direct | direct |
| Attribution groups populated | 3 | 3 |
| Affinity mask / priority | 0xffff / BelowNormal | 0xffff / BelowNormal |

Base-simulation timings, population counts, engine `PlayerLoop`/`GC.Collect` markers, frame-time and memory counters, fixed-step accounting, host facts and ownership counters all exported. Render-dependent markers reported `headless`, as designed; `Physics.*`, `Animator` and per-frame allocation counters reported `unavailable` in this engine build. Attribution resolved prefab names (for example `Beech1`, `Rock_4`, `Fish2`) and routed RPC handler names.

The first-poll cost matches earlier releases' cold-start peaks. Steady-state poll cost is in the same range as 0.4.0 (0.47 ms client, 0.84 ms server). Per-call Harmony overhead of the new probes and `ProfilerRecorder` overhead were not measured; no FPS claim is made.

### Not validated

- Rendered-client behavior: GPU frame time, present wait, draw-call counters and shader-compilation markers need an ordinary graphical session.
- Two-player values: relayed transport, ownership request completion and remote replication bytes need the second client connected.
- Cross-process alignment through `host_qpc_timestamp` needs one known-simultaneous event to confirm.
- Wear/support and terrain-operation costs under a real base: the test world holds only generated objects, so `WearSupportUpdate`, `TerrainCompApply` and station ticks recorded no calls here.
