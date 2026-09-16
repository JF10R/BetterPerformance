# Join-loading research for 0.4.1

Read-only investigation, 2026-09-15. Scope: installed Valheim 1.0.12 client
assembly, Unity 6000.0.75f1, and the two existing isolated 0.4.0 captures below.
No production changes, game launches or benchmarks were performed for this report.
Scout did not index native game methods; exact installed IL was inspected instead.
Evidence is retained locally in ignored `.qa/loading-*.txt` files.

### What the captures actually establish

| Client capture run | First interval | NetworkUpdate, one call | RpcDispatch maximum / sum | IncomingZdoData sum |
| --- | --- | --- | --- | --- |
| `20260915T113624Z-4a308c` | 20.157 s | 19,942.521 ms | 16,463.468 / 19,885.237 ms | 127.373 ms |
| `20260915T115737Z-85e56d` | 18.677 s | 18,447.833 ms | 15,919.650 / 18,393.702 ms | 186.930 ms |

These are inclusive elapsed timers. NetworkUpdate contains RPC work; its time is
not network transit time. RpcDispatch sums cover 89 and 83 calls respectively.
The first run logs world-generator version setup at 07:38:21, then global keys
at 07:38:38. This supports investigating synchronous world initialization inside
join handling; it does not assign the entire 16-second RPC peak to that method.
The existing captures lack WorldGenerator phase timers.

In that first interval, ObjectCreateSorted totals only 0.053 ms and ZoneUpdate
0.009 ms. Thus the initial long callback and later object-loading backlog are
distinct targets. The same log subsequently reports a 180.122-second respawn
wait. That is accumulated game `dt` in the native respawn wait, not whole-join wall
time, not 180 seconds
of one method executing, and not proof of a network bottleneck.

The runs are headless client tests with an evolving synthetic world and installed
mods. They do not establish interactive frame-rate gains or a causal difference
between options. Repeated same-world reconnects and cold starts must be reported
separately; fresh random worlds cannot measure a warm same-world cache benefit.

### Native phase boundaries and interpretation

| Boundary | Verified native work | Useful measurement and limit |
| --- | --- | --- |
| `WorldGenerator.Initialize(World)` | Cleans old river data, constructs a new generator, replaces singleton | One inclusive initialization timer; identify menu versus gameplay without recording private world identifiers |
| `WorldGenerator.Pregenerate()` | FindLakes, PlaceRivers, PlaceStreams(false), PlaceStreams(true) | Inclusive generation timer; optional three child families show where work goes; do not add parent and children |
| `ZoneSystem.Start()` | World rates, location setup, vegetation validation, RPC registration | Main-scene setup; may occur before connection, so not inherently join-response work |
| `ZoneSystem.CreateLocalZones(Vector3)` | Repeated PokeLocalZone over current synchronized simulation area | Main-thread demand scanning and zone pokes; distinguishes repeated demand from successful creation |
| `ZoneSystem.SpawnZone(Vector2s, SpawnMode, out GameObject)` | Terrain readiness, location prefab readiness, instantiate, mode-dependent placement | Inclusive attempt timer plus outcome; false alone does not distinguish terrain from prefab wait |
| `HeightmapBuilder.RequestTerrainSync(...)` | Busy-polls RequestTerrain until a result exists | Main-thread elapsed wait, including lock acquisitions and polling; not terrain-generation CPU |
| `HeightmapBuilder.Build(HMBuildData)` | Terrain data generation on existing worker | Worker elapsed service; queue wait excluded; pair with process/main-thread CPU to investigate contention |
| `Minimap.TryLoadMinimapTextureData(int)` / `GenerateWorldMap()` | Existing disk cache or pixel generation, texture upload, cache write | Separate cache result and inclusive generation; only relevant if invoked during the measured join |

All proposed counters can be fixed-size. Avoid per-height-sample hooks and polling
the builder's lists without its native lock. Hooking the entry/exit of BuildThread
would measure the worker's lifetime rather than individual terrain jobs.

### Ranked work-avoidance candidates

1. **Eliminate wasteful synchronous terrain polling if it is significant.**
   RequestTerrainSync loops directly back to RequestTerrain when it returns null
   (`IL0000..000F`), with no sleep/yield. Every attempt reacquires a monitor and
   scans native ready/pending lists. A conservative bounded yield/backoff between
   unsuccessful polls could reduce CPU and lock contention while retaining the
   same native result, especially with client and server on the same host.
   This is a candidate, not a demonstrated gain: ZoneSystem.SpawnZone already
   calls IsTerrainReady before instantiating, so normal zone creation may reach
   the synchronous path with its terrain ready. Measure calls and elapsed first.
   Sleep granularity and scheduling can increase completion latency; a yield
   does not guarantee another worker runs. Preserve native exceptions and stop
   conditions; do not fabricate terrain readiness.

2. **Reuse exact world-generation results on repeated joins, only after phase
   attribution.** Initialize always constructs a new generator; the non-menu
   constructor calls Pregenerate (`IL01AB..01BE`). FindLakes samples a fixed
   -10,000..10,000 grid at 128-unit steps and merges candidates; streams also
   regenerate. A bounded cache of immutable generation outputs could avoid
   repeated deterministic work. It cannot safely cache only the returned stream
   list: PlaceStreams also calls RenderRivers, and Pregenerate discards the second
   stream list while retaining its side effects. A complete snapshot must cover
   lakes, rivers, streams and river-point structures plus generation inputs.
   Seed alone is insufficient: generation version, compatible code/patch set,
   configuration, world lifetime and complete output ownership matter. The
   constructor also resets biome caches and static noise state. Do not reuse an
   old singleton or bypass initialization wholesale. This is a larger,
   validation-heavy candidate, not an immediately safe shipping cache.

3. **Avoid measured terrain rework with a bounded exact data cache.** Native
   RequestTerrain and IsTerrainReady already deduplicate pending requests with
   HMBuildData.IsEqual; RequestTerrain consumes matching ready entries. BuildThread
   limits the ready queue to 16 entries (`IL00AC..00C7`). First establish whether
   eviction/revisits actually rebuild the same terrain. A larger queue or retained
   cache adds memory and may help revisits, but does not remove first-join unique
   terrain work. Reusing mutable HMBuildData without auditing consumer mutations
   is unsafe. Preserve all native identity inputs, including WorldGenerator.

4. **Use existing minimap caching before adding another full-map cache.**
   Minimap.Update calls TryLoadMinimapTextureData before GenerateWorldMap.
   The cache checks files, seed and version; cache loading includes decompression
   and Texture2D uploads. GenerateWorldMap allocates multiple full-resolution
   arrays and evaluates biome/height/masks per pixel, then uploads and saves them.
   Measure whether this path occurs in the relevant join. If regeneration is
   frequent, diagnose native cache misses and invalidation first. Never preserve
   stale pixels by suppressing native version/seed checks or deleting user caches
   as part of an optimization test.

### Changes that are not currently safe shortcuts

World generation cannot simply move to Task.Run. The constructor and stream
generation manipulate global UnityEngine.Random state; the constructor mutates
static FastNoise and clears static biome dictionaries. Unity documents its Random
API as global state and unusable outside the main thread. Snapshotting genuinely
pure inputs/results is a separate redesign, with exact terrain parity required.
[Unity Random documentation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Random.html)

Heightmap generation is already threaded. BuildThread sleeps 10 ms after every
iteration, even when backlog remains (`IL00D5..00D7`). Reducing that delay may
increase service throughput, but also CPU competition; adding workers additionally
requires proving generator/cache thread safety and atomic job ownership. Neither
change avoids computation. Measure queue pressure and Build service before
changing native pacing on a machine hosting two game processes.

Skipping stationary CreateLocalZones calls is unsafe without stronger evidence:
world/reference position can stay constant while terrain readiness, locations,
TTL or incoming objects change. ZoneSystem.Update runs demand work around a
0.1-second cadence and also advances TTL and prefab lifetimes. Server-only ghost
generation and new-world location generation are separate from client joins;
do not suppress them to make a client stopwatch look better.

Background-loading priority is another tradeoff, not a universal loading fix.
The logs already show native Low/Normal/High transitions. Unity documents that
this controls asynchronous reads/deserialization and main-thread object
integration budgets. It does not make synchronous managed Pregenerate work
asynchronous, and raising it can lengthen main-thread frames.
[Unity background loading priority](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-backgroundLoadingPriority.html)

### Next decision gate

Bind connection/peer-info, world generation, local-zone readiness and local-player
appearance as distinct observed phases. Readiness polling yields bounds at poll
resolution, not exact readiness timestamps. Time native initialization methods
directly because a long synchronous call prevents ordinary polls from running.
Then rank work by attributable elapsed/CPU cost: an inexpensive terrain poll
backoff experiment is justified only if synchronous waits matter; a generation
cache is justified only if generation dominates and reconnect reuse is common.
Neither observation needs to mutate worlds, characters, achievements or network
protocols. No third-party mod compatibility or performance claim was established
by this read-only investigation.
