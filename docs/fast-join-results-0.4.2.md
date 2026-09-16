# Fast-loading results and diagnostics 0.4.2

For the complete native loading dependency chain, read the
[loading-time research reference](valheim-loading-time-analysis.md).

2026-09-15, Valheim 1.0.12 / Unity 6000.0.75f1. **The 1–10-second target has
not been achieved.** A loading-only zone scheduling prototype reduced the
observed loading-screen interval in two exploratory comparisons. It remains
in the isolated QA harness, outside the installed gameplay DLL.

### Measured result

The clock begins at the accepted scene-transition request and ends when the
native loading HUD is first observed released. It excludes application startup,
menus and time spent connecting before that request. HUD release does not prove
the first rendered frame or input response is complete.

| Pair | Baseline | Zone burst | Observed reduction |
| --- | ---: | ---: | ---: |
| A / B | 30.419 s | 24.895 s | 5.525 s / 18.2% |
| C / D | 31.729 s | 27.487 s | 4.242 s / 13.4% |

These are two observations per configuration, not a confidence interval or a
guarantee for ordinary play. Order was baseline/prototype/baseline/prototype.
Tests used the same disposable world/character fixture, effective radius 5,
headless/no graphics, silent audio, explicit 30 Hz pacing, and below-normal
process priority. CPU contention and OS caches were not experimentally controlled;
development tools also ran on the host. No graphical Ultra/FPS conclusion follows.
Each trial restarted the isolated client and server; this is not a resident-world
reconnect benchmark. The native biome cache file existed in every fixture, but
every observed client cache attempt returned false.

Run identities: A `20260915T124437Z-b7360a`, B `20260915T124734Z-1917f0`,
C `20260915T125147Z-fbb515`, D `20260915T125753Z-8fb8b9`.
Raw captures and hashes remain private under `.qa`.

### What changed in the prototype

Native local-zone demand is serviced approximately every 100 ms and registers
at most one new zone per call. Near-object creation waits for the entire active
zone area; the tested nonclassic radius requires 97 zone roots.

The prototype keeps the mandatory native call, then permits up to three extra
calls while the first client spawn is still waiting, with a soft 8 ms total
budget. It stops when a native call cannot create a zone. Native terrain,
resource, collision and area-readiness tests remain intact. No object moves to
a worker thread, and no readiness result is fabricated.

B registered 47 extra zones through additional passes; D registered 48. Their
largest observed burst durations were 12.331 and 13.351 ms. The budget is soft:
one indivisible native operation can cross it. This is the principal scheduling
tradeoff and a reason to validate graphical loading/teleport/death behavior
before shipping an independently configurable production module.

### Newly isolated bottlenecks

- Biome verification/generation cost **5.14–5.55 s inclusive** across these runs.
  Point generation dominates; sectors and cache persistence are nested work.
  These figures cannot be added to their parent PeerInfo duration.
- Client native world version was 0. In D, the existing cache header contained
  41 at the moment of reading, and the native cache returned false. Furthermore,
  version 0 causes native regeneration even after a successful read. See the
  [cache investigation](native-biome-cache-research.md).
- The native spawn gate was **8 game-time seconds**. Preparation runs during
  that interval; deleting the gate would not necessarily save eight seconds.
  Known objects being ready does not establish receipt of the complete initial
  network state. The current implementation preserves the gate.
- Remaining time includes world initialization, zone preparation and object
  materialization. The scene transition itself was approximately 1.66–1.68 s
  in these headless trials; treating all 30 seconds as scene deserialization
  would target the wrong work.

### Routes toward the target

1. Improve loading-only scheduling first: it already has a positive observed
   result and preserves native work. Root preparation and object creation need
   separate allowances, with readiness and worst-frame costs measured.
2. Develop an independently identified derived-data cache. Validate world identity,
   seed, generator version, game/mod algorithm inputs, prior biome state and
   integrity. Compare cached outputs against the native result in shadow mode
   before skipping any generation. Never repair this by rewriting a native
   version header. The measured five-second stage is an opportunity bound,
   not a promised cache gain.
3. Overlap or parallelize only proven independent pure calculations. Native
   terrain already has a worker. Biome sector order, mutable river caches and
   shared random state make naive parallel loops unsafe for deterministic output.
4. For sub-ten-second joins, establish a reliable initial-data completion barrier
   before considering a shorter minimum wait. A server-assisted preload protocol
   or validated resident cache could help, but adds lifecycle and compatibility
   work. Cold joins and warm reconnects must be reported separately.

Unity object integration still has main-thread work; its background loading
priority changes the integration time allowance rather than making every
operation parallel. [Unity loading priority documentation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-backgroundLoadingPriority.html).
Holding scene activation at 90% can block the async operation queue, so it is
not automatically a useful preload mechanism. [Unity activation documentation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AsyncOperation-allowSceneActivation.html).
Additional hypotheses and constraints are in the
[frontier research](fast-loading-frontier-2026-09-15.md).

### Installed diagnostics and verification

Client and server received **0.4.2**, SHA256
`6930827D48B2A64B9D3EE5BB35B9276D9696C7899BB23FB866C511FD85EA4B9D`.
Existing configuration files were preserved byte-for-byte. The new
`Diagnostics.LoadingDetailsEnabled` defaults to true when absent; recording
still follows the existing capture controls. No QA DLL was installed.

The added diagnostics use fixed stage/reason counters and passive native-result
observations. They record biome timings, cache outcomes, native minimum wait and
observed readiness reasons without changing those outcomes. Process-cumulative
snapshots preserve pre-capture work; the report uses the latest snapshot instead
of adding it repeatedly. Counts are not wait durations or saved time.

Verification: plugin/harness builds succeeded, 38 core and 39 Python tests passed,
and 42 loading-detail metadata/classification checks passed on each assembly
set. Standalone CLR cannot exercise all Unity-native interfaces; actual isolated
Unity runs established installation and successful completion. Both roles
reported installed loading details, zero detail-probe failures and zero dropped
records in all four trials. This is not proof of negligible telemetry overhead;
no telemetry-off join comparison was performed.

Observed combined client/server capture volume projected to approximately
36–38 MB/hour for these short loading/idle workloads. A full gameplay session
can differ; existing bounded file/directory controls still apply.

All four clients completed; owned processes stopped and normal protected-save
hashes were unchanged. Only four automatic Unity session bookkeeping preferences
were restored. Trial A lacked the auxiliary server observer because the new
QA branch omitted an end marker; its plugin join timeline completed. Trial B
used a controller-supplied end marker; C and D used the repaired harness and
both auxiliary observers completed. The auxiliary observers cover post-ready
work, not the measured join interval.

No world/character persistence code changed in 0.4.2, no native cache header was
edited, and the prototype is absent from normal installations. Earlier save
backups and 0.4.1 deployment backups remain available; the 0.4.2 deployment also
backed up the prior DLLs and configs. No claim of zero general mod risk is made.

Scout feedback is recorded in the
[updated developer report](scout-feedback-0.4.1.md).
