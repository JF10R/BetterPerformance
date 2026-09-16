# Diagnostic cost reduction — 0.3.2

The normal co-op session cannot be replayed reliably. Version 0.3.2 therefore reduces avoidable collector work directly and adds soft backoff, without requiring a second gameplay pass. It does not establish negligible total runtime overhead.

### Implemented changes

- Keep every instrumented game duration/failure; measure the recorder's own aggregation once per 64 valid records instead of every record. That sampled self-measurement now excludes lock acquisition. Its total and percentiles are not directly comparable to the old recorder self-timing.
- Reduce loot scans from a 100 ms default, 256 candidates and a soft 0.5 ms allowance to 250 ms, 128 candidates and 0.2 ms. Scans exceeding 0.5 ms impose at least a one-second cooldown. Skip clocks/scene lookups for untracked creations.
- If complete main-thread polling and export enqueueing exceed 2 ms, double the next interval up to 10 seconds; recover after 30 cheap polls. Method/loop aggregation remains active while point-in-time context becomes less frequent.
- Request below-normal priority for the background writer; report actual request success and keep bounded queues/file storage.

These changes trade some sampled loot/context coverage for lower collection frequency. They cannot preempt a single expensive lookup, OS call, lock wait or GC. Counters expose overruns and reduced coverage. [The session guide](play-session.md) defines the configuration and interpretation.

### Offline hot-path comparison

Only `MetricBook.Record` was benchmarked, using standalone x64 executables with identical workloads. Seven adjacent pairs per runtime alternated baseline/candidate order. Each process warmed up with one million calls, then timed ten million direct calls over eight fixed metric/duration pairs on one thread. The monitor lock was uncontended; construction, draining, export and file I/O were outside the measured region.

| Runtime | 0.3.1 mean ns/record | 0.3.2 mean ns/record | Reduction | Observed paired delta range |
| --- | ---: | ---: | ---: | --- |
| .NET Framework CLR, net472 target | 76.076 | 18.126 | 76.17% | -60.192 to -56.201 ns |
| .NET 10.0.8 | 64.047 | 14.393 | 77.53% | -51.620 to -47.448 ns |

All 28 runs reported zero managed bytes allocated per record. Both versions already avoided hot-path allocation; the improvement is reduced CPU work. Output sample counts were verified. The production-path allocation regression also passes after warmup.

The observed ranges are not confidence bounds. Machine load and scheduling were uncontrolled, there was no contention scenario, and tiered compilation was disabled for execution. The repeating workload aligns recorder self-samples with a fixed point in the sequence. This demonstrates a reduction in one component, not a 76% reduction in total logging, an FPS gain, a Unity Mono measurement or a multiplayer latency improvement. Harmony hooks, OS polling, snapshot allocations and background serialization remain outside this benchmark.

Source SHA-256 identities:

- Baseline `MetricBook.cs`: `FFFF55B3E8167C08DC2D0A66B97C9F9E22ECF27718A39B197D5EFB9938BB1AB9`.
- Candidate `MetricBook.cs`: `539B5A30E541222AC0B08ACB9FF7237724BF88006FA9F8EE1349DB30BA3CD6CC`.

Raw measurements and the offline runner remain local under `.qa/overhead-032/`. No Valheim process or QA harness ran for this change. The next normal session can expose collector overruns and useful game bottlenecks, but cannot by itself isolate the complete instrumentation tax.
