# Initial loading validation: 0.4.3

2026-09-15. Client and dedicated server received the same verified DLL:
`55EC81019E9E9B05CA6F81C7183EEBED7E21BA302CA71205CD000EE4ED638BD9`.
`InitialLoading.Enabled` is true on the user's client and false on the server.
All other configuration values were preserved. Prior DLLs/configurations were
backed up before replacement; no QA DLL was installed.

### Isolated Unity verification

Valheim 1.0.12 / Unity 6000.0.75f1, existing BetterNetworking/VPlus/plant plugins,
the established disposable world and temporary character fixture. Effective
radius 5, silent headless client, 30 Hz pacing, two Unity job workers and
below-normal process priority. Production module enabled first, then disabled
in a fresh client/server run. The historical QA acceleration wrapper was not
enabled in either trial, so there was no double acceleration.

| Observation | Enabled | Disabled |
| --- | ---: | ---: |
| Transition → observed HUD release | 26.805 s | 32.830 s |
| Respawn start → accepted spawn point | 12.830 s | 19.363 s |
| Additional native calls | 48 | No acceleration patch installed |
| Additional successful zone registrations | 47 | No acceleration patch installed |
| Eligible native pass windows | 103 | Not collected by this module |
| Cumulative eligible pass-window time | 529.858 ms | Not collected by this module |
| Maximum eligible pass-window time | 14.405 ms | Not collected by this module |
| Cumulative additional native-call time | 245.092 ms | Not collected by this module |
| Maximum additional native-call time | 7.308 ms | Not collected by this module |
| Native failures recorded by acceleration | 0 | Not applicable |
| Dropped capture records, both roles | 0 | 0 |

The observed total difference is **6.025 seconds / 18.4%** in this single pair.
It corroborates the earlier prototype's direction, but is not a confidence
interval, an unmodded benchmark or a guaranteed graphical-client gain. Host
load and OS caches were not controlled, and test order was not randomized.
The native eight-game-second minimum remained intact. Both clients encountered
the previously documented native biome cache-version mismatch.

The additional 245 ms is work serviced earlier, not time eliminated. Costs
overlap native zone timers. The first eligibility check is outside the measured
window; subsequent compatibility checks are inside it. Patch inspection can
allocate during initial loading. No instrumentation-off comparison establishes
its isolated overhead, and no allocation-free claim is made.

The enabled run retained 50 mandatory successes plus 47 extra successes. It
recorded 54 no-progress stops and 49 time-budget stops. Zero pass-limit and
context stops were observed; those paths were not exercised by these live runs.
The soft 8 ms allowance can be exceeded by one indivisible native operation.

Both clients completed, both server observers finished, owned processes stopped,
and protected normal save hashes matched. Four automatic Unity session
bookkeeping preference changes were restored after each run. The user's worlds
and characters were not loaded by these tests.

Private reproducibility artifacts:

- Enabled: `.qa/runs/20260915T132855Z-9a0051`.
- Disabled: `.qa/runs/20260915T133134Z-c76c7a`.
- Summarized evidence: `.qa/initial-loading-validation-043.json`.
- Deployment receipt: `.qa/deployment-0.4.3.json`.

### Offline verification and limits

- Plugin and harness builds: zero warnings/errors.
- Core suite: 38 passing tests; Python reporting suite: 41 passing tests.
- Initial loading native-contract/IL suite: 319 checks on each installed assembly
  set. Includes native fingerprints, changed-body rejection, exact call-stack
  shape, original instruction identity/order/labels/exception-block preservation,
  and rejection of missing/ambiguous callsites.
- Existing game-contract checks passed where the standalone runtime supports
  them. Unity interface/JIT limitations remain explicitly reported as unavailable
  or static-only; these are not disguised as live verification.
- Independent source review found no gameplay blocker. The production loop
  preserves mandatory calls, true results, exception propagation and nesting
  cleanup, with configuration/context checks before each extra pass.

Graphical loading-frame behavior, real-session user feedback, live configuration
changes mid-burst, interrupted connections and death/teleport scenarios have not
been independently exercised by this validation. The initial-spawn and context
guards constrain applicability; they are not a claim of universal mod compatibility.

See [configuration and measurement semantics](initial-loading.md) for the single
disable switch and [native loading analysis](valheim-loading-time-analysis.md)
for the underlying dependencies.
