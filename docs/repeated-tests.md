# Repeated-test protocol

### Questions

1. What changes when diagnostics 0.1.0 is replaced with 0.1.1 at Ultra simulation distance?
2. With diagnostics 0.1.1 fixed, how do Classic and Ultra simulation distances differ in this workload?

Neither comparison tests a new gameplay optimization. BetterNetworking 2.3.3 stays enabled in every arm. This protocol cannot quantify its benefit against vanilla networking or the total cost of having any diagnostics installed.

### Conditions

The original protocol planned three blocks, each containing these arms:

| Arm | Diagnostics | Simulation |
| --- | --- | --- |
| `old_ultra` | 0.1.0 | Near 5, far extension 2, non-Classic |
| `new_ultra` | 0.1.1 | Near 5, far extension 2, non-Classic |
| `new_classic` | 0.1.1 | Near 2, far extension 2, Classic |

Execution order is rotated: new Ultra / old Ultra / new Classic; old Ultra / new Classic / new Ultra; new Classic / new Ultra / old Ultra. This reduces simple position effects but does not eliminate drift or random simulation differences.

During execution, the user requested fewer restarts and a prolonged session. Two complete blocks were retained (six process-pair runs); the third was canceled. A separate ten-minute Ultra workload was then requested in one continuous session. Its windows are descriptive observations within one session, not additional independent repetitions for the comparison. The change was made for that operational preference, not because a significance threshold was reached.

Each process pair receives a fresh copy of the same temporary-world fixture and character. The fixture contains some previously generated areas. It is not a developed production world, and restored files do not guarantee deterministic AI, scheduling, terrain-loading caches or network traffic. Initial state, plugin DLL hashes and configurations must be checked before accepting the results.

The local QA harness uses batch mode without graphics, muted client audio, 30 Hz pacing, BelowNormal priority and two Unity worker threads per process. Another game may remain running. Those restrictions protect foreground use but make this a simulation/measurement experiment, not a graphics or ordinary gameplay FPS benchmark.

After joining with the cloned temporary character, wait 15 seconds, spawn 32 Wood drops, wait 15 seconds, remove the tracked drops and request an asynchronous save, wait 10 seconds, traverse 128 m, wait 20 seconds, traverse 256 m, then wait 20 seconds. Loading and warmup are excluded from the independent observer's approximately 65-second workload window. Native saves and object behavior remain enabled; additional native saves may occur.

### Observation and uncertainty

A separate, unchanged QA observer records loop gaps into a bounded 12,000-element buffer in every arm, writing once after the workload. The server starts/stops its observer when it sees local control markers, so its boundaries are approximate and can differ by a frame or a stall. No frame gap is treated as an independent experimental repetition.

The comparison script requires either 12 or 18 observer files: two roles, three arms and two or three complete blocks. It rejects duplicates, incomplete designs, truncated buffers and nonpositive/nonfinite loop samples. Fields are `Role` (`client`/`server`), `Variant` (table above), `Block` (1–2 or 1–3), `FramesMs` (positive millisecond samples) and `Truncated` (false). QA files also include UTC boundaries and scenario markers; the experiment record verifies these separately.

Run-level metrics are mean loop gap, nearest-rank p95, maximum gap and gaps over 50 ms per observed minute. The primary stall indicator is gaps over 50 ms per minute; other metrics help interpretation. Averages near 33 ms largely reflect the explicit frame pacing.

For each block, subtract baseline from candidate. Report the mean difference, observed difference range and an exploratory 95% paired Student t interval: mean difference ± critical value × sample standard deviation of the differences / sqrt(n). The critical value is 12.7062047364 for two pairs (df=1), or 4.3026527299 for three pairs (df=2). With so few degrees of freedom and unverified normality/independence, these intervals are weak evidence and often wide. They are not corrected for multiple metrics. An interval crossing zero supports an inconclusive result, not proof of no overhead. Repeating under different workloads and with more blocks is necessary before broad claims.

The earlier single random-world capture is an exploratory reference only. Different pacing, routes, loading state and duration prevent a valid numerical before/after claim against it.

The uncertainty calculation follows the [paired-observation interval formula](https://www.itl.nist.gov/div898/handbook/prc/section3/prc312.htm). Its assumptions are especially consequential with this small sample.

### Acceptance

- All phases finish and both processes stop cleanly.
- Every accepted capture has matching accepted/written records, zero drops and a completion footer.
- Requested simulation distance is verified, and the same DLL hashes are used within each arm.
- Existing protected world files remain unchanged; no original installation is modified.
- Setup/debug pilots and incomplete runs are excluded explicitly.
- Report failures, external-load limitations and unavailable metrics alongside results.

The authorized local runner, test worlds, proprietary assets and raw captures remain outside Git under `.qa/`. The public comparison script does not start applications, access production saves or upload files.
