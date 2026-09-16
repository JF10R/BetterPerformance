# CPU, memory and bounded parallelism investigation

## Finding

There is a credible worker boundary after an independent byte snapshot exists. There is no equally small, safe change that moves Valheim's current AI, collision queries, object creation, or save preparation wholesale to background threads. Vanilla already has a world-save worker. Repeating the same compression less often, reducing serialization work, and measuring CPU occupancy should precede adding workers to the host running both client and server.

This investigation adds an **isolated research prototype**, not a shipping worker framework or a live save patch. It does not open worlds, launch Valheim, write saves, or invoke achievement/gameplay APIs. The prototype lives under ignored `.qa/parallelism-20260915/`; production source and package dependencies are unchanged by this stream.

## Installed implementation inspected

Read-only local inspection on 2026-09-15 found Unity **6000.0.75f1 (26349cd2a5c8)** in both client and dedicated-server `UnityPlayer.dll`. The client includes `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`. The desktop benchmark runtimes below are separate runtimes; using the same C# target framework does not reproduce Unity Mono's JIT, compression library, garbage collector, or engine contention.

Current client assembly hashes:

- `assembly_valheim.dll`: `27A766A8D23A7BD8B6A54FB9AD0452A96C305FB3629B39C40527C09A1C393A84`.
- `assembly_utils.dll`: `9333361C9D2A2A941C1E6D47E76220689FED1761DE8B5D123F7F42F26BE3132A`.

Scout located the existing map serialization optimization. Direct local IL inspection was required for installed game assemblies outside the repository index. The evidence is retained in the fixture's `*-current-il.txt` files.

| Boundary | Verified behavior | Parallelism implication |
| --- | --- | --- |
| `Minimap.GetMapData` | Reads explored bit arrays, mutable pin fields, and `ZNet.IsReferencePositionPublic`; builds a package, then calls `WriteCompressed`; returns `byte[]` synchronously. | Snapshot those values at a coherent main-thread boundary. Calling the entire method from a worker is unsafe. Replacing only compression with dispatch followed by immediate waiting does not release its synchronous caller. |
| `ZPackage.WriteCompressed` | Calls source `GetArray`, then `Utils.Compress(byte[])`, then writes compressed length and bytes to its own writer. | A private source byte array is a plausible pure-data input. Concurrent access to the package or writer is not established as safe. |
| `Utils.Compress` | In `assembly_utils.dll`; local `MemoryStream` and `GZipStream` with `CompressionLevel.Fastest`, followed by `ToArray`. No Unity calls in the inspected body. | Independent complete inputs can use independent compressor/stream instances. This is not proof that one gzip stream can be split into arbitrary concurrent pieces without changing format. |
| `ZNet.SaveWorld` | Prepares ZDO, zone and event snapshots, then starts `SaveWorldThread`; joins a previous active save, and optionally joins the new save. | World save is already partly multithreaded. Measure preparation, worker time and joins separately before deciding which phase matters. |
| `ZDOMan.GetSaveClonePerChunk` | Reads live sector lists/counts, dirty flags, clones ZDOs and updates save metadata. `PrepareSave` also calls `BeginSave` and `ZDOExtraData.PrepareSave`. | A shallow copy of a list is not a coherent snapshot of all mutable ZDO/extra-data state. Parallel clone/serialization needs a proven ownership and mutation contract. |
| `BaseAI.UpdateAI` | Reads validity and network ownership, updates alert state and timers, and invokes regeneration/takeoff/landing work. | Entity ownership can change while a worker runs. AI batches contain state changes, not just independent arithmetic. |
| `Pathfinding.GetPath` | Mutates the caller's path list, pokes areas, snaps positions, calls `NavMesh.CalculatePath`, and uses shared `m_path`. Cleanup uses shared `optPath`/`tempPath`. | Concurrent calls would race even before considering Unity API restrictions. Replacing a list with a concurrent collection would not solve native affinity or shared path state. |

## Engine constraints and memory cost

Most Unity APIs require the main thread. An asynchronous method can still resume there through Unity's synchronization context; `async` alone does not make CPU work parallel. A worker result must return through an explicit commit boundary, where object existence, world epoch and ownership are checked again. The pipeline must preserve ordering and exceptions. [Unity continuation and thread-affinity documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/async-awaitable-continuations.html).

For a future pure AI scoring experiment, a main-thread snapshot could contain primitive positions, health, faction flags and version tokens. Workers could score candidates, followed by main-thread revalidation and application. This is a larger behavioral change: stale positions, target death, ownership handoff, random-state consumption and different update order can alter gameplay. Current batch measurements do not yet isolate enough pure calculation to justify that rewrite.

Unity supports asynchronous collision query batches through `RaycastCommand`; this is a documented job API, not permission to run arbitrary `Physics`, `Transform`, `NavMesh`, or GameObject calls on a managed worker. Query masks, trigger handling, result lifetimes and the physics snapshot must match the original behavior. Waiting immediately for completion removes useful overlap. A collision rewrite is therefore a separate measured experiment, not a patch around every raycast. [Unity RaycastCommand API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/RaycastCommand.html).

Burst/job optimizations need a compatible shipped runtime and unmanaged data layout. Managed ZDO graphs, dictionaries, strings, and `GZipStream` are not directly usable as Burst inputs. Copying into native containers and copying back are real costs; installing a mod cannot automatically recompile all vanilla engine code with Burst. [Unity job-system data constraints](https://docs.unity3d.com/6000.0/Documentation/Manual/job-system-thread-safe-types.html).

More workers can reduce elapsed batch time while retaining more simultaneous inputs, outputs, compressor state and thread stacks. Snapshot ownership prevents races by paying for a copy. Pooling can reduce allocation rate but retains memory, and returning a rented buffer before every consumer finishes creates corruption. A retained cache also has to count **input plus compressed output**, not just one side of the cache. Unity uses Boehm-Demers-Weiser GC; incremental collection spreads work across frames and does not eliminate total collection cost. Desktop CLR large-object-heap behavior is not an adequate model of Unity memory pressure. [Unity garbage-collection modes](https://docs.unity3d.com/6000.0/Documentation/Manual/performance-incremental-garbage-collection.html).

## Host client and server

The inspected host has a Ryzen 7 5800X3D, 8 physical cores and 16 logical processors. Two Valheim processes share those execution resources, caches and memory bandwidth alongside existing engine jobs, rendering, native save workers and other applications. Four extra workers in each process would be eight additional runnable workers, not eight free physical cores. SMT threads are not equivalent to independent cores. Do not set worker count independently to `Environment.ProcessorCount` in both processes.

The decision must use both processes' main-thread CPU fraction, process CPU, private commit/working set, Unity used/reserved memory, GC activity, frame timing and operation durations. Low main-thread CPU during a stall cannot by itself identify scheduler starvation; sleep, locks, rendering and I/O can produce the same residual. A CPU-heavy desktop microbenchmark with no controlled client/server load establishes neither spare CPU capacity nor gameplay latency. .NET guidance likewise recommends measuring parallel overhead, avoiding shared mutable state and limiting over-parallelization. [Microsoft parallelism pitfalls](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/potential-pitfalls-in-data-and-task-parallelism).

## Immutable snapshot prototype

The standalone x64 fixture compares 0, 1, 2 and 4 dedicated workers. Zero means synchronous copy/compress; one means genuine offload but no compression parallelism. Each batch contains eight **independent** buffers, each either 256 KiB or 4 MiB. Inputs are deterministic sparse runs or pseudorandom bytes, not sampled player/world data. Each buffer is copied before worker access and compressed as a separate gzip member, using `Fastest` and a private stream. Outputs stay in input order. There is no format change proposed for Valheim.

Admission occurs before copying. At most twice the worker count of snapshots can be active or queued; the batch contains at most eight inputs. The queue itself is capped at the worker count. At 4 MiB per input, the copied-input bounds are 4/8/16/32 MiB for 0/1/2/4 workers. Those bounds exclude the original 32 MiB batch, retained outputs, temporary stream buffers, native compressor allocations and thread stacks. Output retention is bounded by the fixed batch, not an unlimited producer queue.

Two warmup batches and their round-trip verification precede one measured batch per fresh process. Each condition has seven repetitions, alternating worker order forward/reverse. Construction, warmup, forced pretrial GC and decompression verification are outside the measured window; input cloning, admission waits, dispatch, compression, stream growth, `ToArray` and final completion wait are inside. This is steady-pool throughput, not cold worker-start latency. Seven alternating repetitions reduce simple order bias but are not independent randomized gameplay trials or confidence intervals.

The fixture records total wall time, producer time including backpressure, copy time, item latency from admission attempt to completion, process CPU delta, private-commit endpoints, retained snapshot peak and output bytes. Managed allocation is the sum of producer allocation plus allocations inside each compression call; worker queue-enumerator bookkeeping and native compressor allocations are excluded. Private commit is measured before/after, **not peak batch memory**, and may retain previously committed runtime heap. CPU accounting can be coarse for short batches. No graphics, GPU, gameplay, network latency, or co-host contention measurement is performed.

Correctness checks cover decompression equality, output ordering, worker reuse, bounded copied inputs, original-input mutation after copying, and propagation of injected worker errors after admitted work drains. They pass on desktop .NET Framework CLR and .NET 10. This is not a production cancellation, shutdown, OOM, or save-recovery contract; the prototype is deliberately not shipped.

### Desktop results

All 224 measured batches completed with round-trip verification: 2 runtimes × 2 sizes × 2 distributions × 4 worker counts × 7 repetitions. The table shows eight 4 MiB inputs (32 MiB source bytes per batch). Ranges are observed minima/maxima, not confidence intervals. `4.0.30319.42000` is desktop .NET Framework CLR; `10.0.8` is desktop .NET 10. No Unity performance inference is made from their large differences.

| Runtime | Input | Workers | Batch wall ms, median [range] | Process CPU ms, median | Managed allocation MiB | Peak copied input MiB |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 10.0.8 | random | 0 | 467.69 [411.22, 545.13] | 421.88 | 193.69 | 4 |
| 10.0.8 | random | 1 | 461.25 [425.18, 502.24] | 421.88 | 193.69 | 8 |
| 10.0.8 | random | 2 | 242.78 [212.75, 338.10] | 406.25 | 193.69 | 16 |
| 10.0.8 | random | 4 | 139.89 [131.49, 151.76] | 390.62 | 193.69 | 32 |
| 10.0.8 | sparse | 0 | 22.44 [17.65, 36.63] | 31.25 | 33.26 | 4 |
| 10.0.8 | sparse | 1 | 15.49 [13.17, 36.66] | 31.25 | 33.26 | 8 |
| 10.0.8 | sparse | 2 | 14.47 [11.55, 21.76] | 31.25 | 33.26 | 12 |
| 10.0.8 | sparse | 4 | 13.49 [11.21, 27.46] | 0.00 | 33.26 | 12 |
| 4.0.30319.42000 | random | 0 | 1121.95 [1010.99, 1399.20] | 1015.62 | 128.10 | 4 |
| 4.0.30319.42000 | random | 1 | 1192.19 [1048.89, 1865.74] | 1015.62 | 128.10 | 8 |
| 4.0.30319.42000 | random | 2 | 619.17 [568.43, 1402.61] | 1031.25 | 128.10 | 16 |
| 4.0.30319.42000 | random | 4 | 396.85 [298.59, 680.58] | 1171.88 | 128.10 | 32 |
| 4.0.30319.42000 | sparse | 0 | 165.50 [154.07, 190.08] | 171.88 | 32.66 | 4 |
| 4.0.30319.42000 | sparse | 1 | 166.53 [141.52, 199.43] | 171.88 | 32.66 | 8 |
| 4.0.30319.42000 | sparse | 2 | 79.73 [77.48, 105.48] | 156.25 | 32.66 | 16 |
| 4.0.30319.42000 | sparse | 4 | 45.73 [42.85, 58.65] | 156.25 | 32.66 | 32 |

For random 4 MiB inputs, moving from zero to four workers changes median batch wall time from 1121.95 to 396.85 ms on desktop CLR, and 467.69 to 139.89 ms on .NET 10. Paired per-repetition changes have medians of -64.63% and -70.95%; observed ranges are [-78.36%, -47.14%] and [-75.13%, -63.40%]. That is evidence for parallel **batch compression** in this fixture, not a predicted Valheim FPS gain. One worker does not clearly accelerate the large random batch: its paired ranges include slowdowns in both runtimes. The CLR two-worker condition also contains a large outlier, with one paired result 36.24% slower than synchronous.

Copying remains material: the large random batch's median copy time is 9.71/5.27/5.62/5.54 ms for 0/1/2/4 workers on CLR and 7.94/3.85/6.48/8.31 ms on .NET 10. Four workers submit the bounded batch in median 5.86 ms (CLR) or 8.42 ms (.NET 10), but the synchronous caller still waits until the reported batch completion. Lower producer time is useful only if an actual caller can do unrelated work before requiring the result.

Throughput and item latency differ. The median of each random large batch's maximum item latency rises from 157.76 ms at zero workers to 394.63 ms at four on CLR, and from 62.17 to 130.46 ms on .NET 10. Those item intervals include admission/queue waits; they are not loot latency or a stable p99 estimate from eight items. Faster batch completion does not mean every queued item finishes sooner.

Memory does not become free: peak copied inputs reach 32 MiB instead of 4 MiB, while measured managed allocation per large random batch stays approximately 128.10 MiB on CLR or 193.69 MiB on .NET 10. These are different runtime compressor/stream allocation behaviors, not a claim that extra workers allocate 65 MiB more in Unity. Median post-batch private commit is 152.52 MiB at zero versus 200.35 MiB at four workers on CLR; .NET 10 reports 153.69 versus 208.49 MiB. Warmup and runtime heap retention affect these endpoint values, so they must not be interpreted as exact peak deltas caused by worker count.

The CLR random batch also uses more median process CPU at four workers (1171.88 versus 1015.62 ms), despite finishing sooner. CPU accounting granularity makes some short .NET 10 sparse batches report zero CPU milliseconds; this means the process counter cannot resolve the interval, not free computation. For small sparse batches, .NET 10's four-worker median is 1.30 ms versus 1.26 ms synchronous, showing that adding workers can lose on cheap work. The benchmark does not control other host activity; these ranges characterize this run and do not prove an optimal worker count.

Reproduction artifacts: `.qa/parallelism-20260915/Parallelism.csproj`, `Program.cs`, `run.ps1`, `records.csv`, `summarize.py`, `summary.json`, and saved IL evidence. Build with `dotnet build .qa/parallelism-20260915/Parallelism.csproj -c Release`; run each executable without arguments for correctness checks, then use the fixture's `run.ps1`. These ignored local files are research artifacts, not packaged plugin code.


## Ranked next boundaries

| Rank | Candidate | Decision-changing evidence and constraint |
| --- | --- | --- |
| 1 | Exact repeated compression avoidance | Record input/output bytes, cache hit rate, compression time and retained memory. A byte-identical hit can avoid CPU without extra workers. Preserve fresh output ownership and native serialization/save behavior. Misses still pay comparison/copy costs. |
| 2 | One worker for independent telemetry export or other optional immutable analysis | Existing capture export already has a background writer; do not create a second pool for work it can safely own. Prioritize producer allocations and bounded queues. If work becomes late, telemetry may be dropped with explicit counts; game saves cannot use that policy. |
| 3 | Independent save-chunk compression after a complete native snapshot | Potential batch parallelism, but the current save worker already owns this phase. First isolate compression from disk/cloud/backup time and verify each chunk's immutable data and ordered commit contract. Start a future in-game experiment at one extra worker, then two; desktop throughput is insufficient to select four. |
| 4 | Pure AI candidate scoring or batched collision queries | Requires extraction of immutable primitives, lifetime/version checks and a delayed commit protocol. Must measure snapshot/application cost and gameplay equivalence. Current shared path state and immediate query results prevent a small transparent offload. |
| Reject as a quick change | `Task.Run(UpdateAI)`, `Parallel.ForEach` over live ZDOs, asynchronous Instantiate/Destroy, or moving `PrepareSave`/save I/O wholesale | Races, native affinity, ownership changes, save consistency and API completion contracts dominate. A high desktop speedup does not validate these changes. |

Promotion requires an isolated Unity test with the actual host client and server together: preserved outputs/ordering, bounded memory, reduced relevant main-thread stalls, no worse loot/action tails, and no save/ownership regression. Compare total CPU and latency as well as throughput. If CPU capacity is already consumed, reducing repeated work or allocations has a better chance than adding runnable workers.
