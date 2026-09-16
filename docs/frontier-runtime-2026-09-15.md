# BetterPerformance 0.3.3: isolated runtime results

### Outcome

Bulk minimap serialization reduced local character-save time in the tested Unity session while preserving the complete decompressed map payload. The temporary character reloaded successfully. All requested timing/AI probes installed on both processes, configuration transitions were captured, and main-thread CPU sampling was available.

The exact tested DLL was installed on the usual client and dedicated server after both test processes exited. This is a limited runtime validation, not proof of every world, mod combination, cloud-save path or long gameplay session.

### Environment and isolation

- Valheim 1.0.12, BepInEx 5.4.23.5, BetterPerformance 0.3.3; BetterNetworking 2.3.3, ValheimPlus 0.10.1.2, PlantEverything 1.21.2 and PlantEasily 2.2.0 alongside.
- Same host client/server, Windows, Ryzen 7 5800X3D. Separate copied runtime/configuration and disposable `bp_test_` fixture world with temporary `perf01`.
- `-batchmode -nographics -nosound`, hidden windows, below-normal priority, two job workers and explicit approximately 30 Hz harness pacing. These results do not measure rendering, GPU load or normal gameplay FPS.
- One session, test root `.qa/runs/20260915T015710Z-08889f`. Captures lasted approximately 124 seconds client and 153 seconds server including startup/shutdown. The active controlled workload ran approximately 71 seconds.
- The controller's preference guard initially failed because Unity updated four automatic session bookkeeping values before the harness could intercept settings. Only those four values were restored after confirming no concurrent change. Full registry export then matched the initial hash; all 168 protected save files matched their initial hashes. No graphics preferences or existing world/character contents changed.
- Test client/server exited; the harness was not deployed to either usual installation. No achievement hooks are included in BetterPerformance.

### Actual game timings

Counterbalanced native/fast calls inside one session. Native here means this optimization switched off, with the same other mods and telemetry active. It is not a pristine unmodded baseline. Warmup/parity occurred before timed blocks; no forced GC inside timed calls. The complete map operation includes native compression. The local character save includes the native synchronous save path.

| Operation | Paired blocks | Native median ms | Fast median ms | Median reduction | Native range ms | Fast range ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `Minimap.GetMapData` | 8 | 181.33 | 51.71 | 71.5% | 176.42–247.01 | 48.18–73.46 |
| `Game.SavePlayerProfile(true, false)` | 6 | 189.92 | 54.70 | 71.2% | 185.58–253.73 | 50.96–95.90 |

Mean paired differences, fast minus native, were **−133.01 ms** for map serialization and **−143.28 ms** for local character saving. Exploratory 95% paired Student-t intervals were respectively **[−156.71, −109.31] ms** and **[−179.81, −106.75] ms**. These intervals describe variation within this run under approximate independence/normality assumptions. They do not cover variation across real worlds, cloud storage, sessions, machines or concurrent players.

Native/fast live-map calls produced **8,388,617 identical decompressed bytes**, including the native envelope payload, metadata and the fixture's pin state. The temporary character was saved with the fast path, reloaded through the normal profile loader and its map compared again. The fixture does not establish correctness for every possible pin/mod mutation; pin serialization itself remains native. Synthetic bit-pattern tests cover dense, sparse, random, partial/chunk boundaries, failure and reentrancy cases separately.

The optimized native map still takes approximately 52 ms here. The patch does not make saves fully asynchronous or eliminate every pause. It leaves world-save preparation, hashing, compression, disk/cloud handling and backups under the game's normal control. Dedicated servers did not serialize a character minimap in this workload, so no dedicated-server saving gain is claimed.

### What the new probes demonstrated

**Graphics:** captured active and raw setting transitions `6 → 3 → 4 → 2 → 6` through the application event, plus one polled frame-limit change. These numeric choices are not relabeled as verified UI presets. The harness held synchronized simulation radius at five sectors, demonstrating why local graphics and synchronized simulation need separate fields. This was a configuration-observation test, not a view-distance performance comparison.

**AI:** 2,186 client batches were observed, with input population up to 37; twelve controlled Greydwarfs were added during the workload. The client handled 250 path queries, five returning false, and six observed spawn calls. The dedicated server had zero AI input population/path queries/spawn-list calls while its aggregate dispatcher still ran. This supports measuring ownership-dependent work on clients as well as the server; it is not a general claim that servers never run AI.

The injected 300 ms main-thread sleep produced a **333.29 ms AI wall gap** and **338.65 ms loop gap** in the corresponding phase interval, with three repeated-frame batches. The sensor therefore responds to known starvation. The test does not prove a cause for the user's previous gameplay issues or improve AI behavior. Whole-session maxima include startup and other phases and must not be attributed to the injected stall.

**Object budget:** observed 312 deferred tracks, 309 completions and three censored tracks; no capacity skips and no pending tracks after finalization. Completion-weighted observed wait after a yield was **53.46 ms**, maximum **785.18 ms**. These are selected near/distant candidates, not a representative loot sample or causal added latency. Six individual creations exceeded the nominal four-millisecond allowance; one reached **13.25 ms**. A soft loop budget cannot preempt an expensive native creation already in progress.

**CPU and RPC:** all 30 `probe.*` entries reported enabled on both roles; no recorded probe failures or failed measured calls were found. Main-thread CPU status advanced from warmup to available. The client reached 97.7% charged CPU in one sampled window while the server maximum was 23.3%; window averages and headless pacing limit interpretation. A large startup RPC includes world-join work and must not be presented as steady-state gameplay latency.

### Logging and measurement cost

The session produced approximately **1.50 MB combined** and zero dropped records, ending with complete writer footers on both roles. Whole-capture rates extrapolate to about **39.2 MB/hour combined**, influenced by adaptive polling backoff during startup. At the configured two-second cadence, the mean interval sizes project approximately **65.4 MB/hour combined**; extrapolating the largest observed interval per role gives about **69.7 MB/hour**. These are workload estimates, not an enforced universal 100 MB/hour guarantee.

The client collector averaged 0.399 ms across 36 polls, with a 10.705 ms first expensive poll; the server averaged 0.305 ms across 45, with a 9.698 ms maximum. Excluding each first expensive poll leaves approximately **0.105 ms client / 0.091 ms server per poll**. Backoff responded to the initial costs. Graphics observation averaged about 0.060 ms client including its first use. These are measured portions of instrumentation, not its full cost.

`TimingRecorder` samples only part of aggregation and excludes lock acquisition and Harmony dispatch. Systematic every-64 sampling can alias method order. Do not multiply it into a claimed total instrumentation overhead or infer an FPS loss. There was no matched diagnostics-on/off gameplay experiment here. Fixed counters, bounded trackers and asynchronous export reduce risk; some hot-path observation cost remains.

### Offline checks and deployment

- 34/34 game-independent test groups; 22/22 Python report tests.
- Client/server reference builds: zero warnings/errors.
- Each installed assembly: 496 existing checks, AI/signature checks, 173 map transformation/compatibility checks. Standalone CLR limitations for Unity interface/native code are explicitly reported; actual Unity startup then installed all probes successfully.
- Exact deployed/runtime-tested SHA256: `4169B528B4FF20955EBC16F438E8FA2B7C4F199C998DD23285F1AB14E18B13DB`; file version `0.3.3.0`.
- Client: map optimization enabled; existing object budget/quota settings retained; loot priority remains disabled. Server: diagnostics enabled, object budget unchanged/off, map optimization off because this dedicated role does not perform the tested character minimap work.
- Both retain continuous capture, two-second configured polling, 30-minute segments, 32 MiB file allowance and 512 MiB directory allowance. At capacity, recording stops rather than deleting old captures.
- Previous DLL/config backups: `.qa/deployment-backups/20260915T020312Z-0.3.3/`; machine-local deployment receipt: `.qa/deployment-0.3.3.json`.

For the next ordinary session, start the usual server and join with the usual client. No console command, QA plugin or second gameplay pass is needed. Another client needs BetterPerformance to measure its own local stalls or benefit from its own map-save optimization; the server cannot apply that client-local change remotely.

### Evidence and next test

Raw captures, phase markers, paired timings, parity/readback markers, protected-state manifests and isolated harness logs remain under the ignored test root above. `.qa/frontier-analysis.json` contains the generated aggregates, and `.qa/analyze-frontier.py` reproduces them. No saves, private raw logs or game assemblies are published.

The next real-session question is whether map serialization remains dominant on the user's larger map and whether graphics/AI/budget context explains the non-save bursts. Retain native compression and persistence until that evidence justifies another change. The broader [research report](frontier-research-2026-09-15.md) ranks the alternatives and falsifying experiments.
