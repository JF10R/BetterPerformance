# Clutter late-update spikes: mechanism and slicing options

Research note, 2026-09-17. Read-only study of the game's own `ClutterSystem` against the
2026-09-16 client capture (158 min, 165 fps): `ClutterLateUpdate` ran 1,564,884 times, sum
41.9 s (0.44 % of wall, mean 26.8 us), max 59.8 ms, 32 three-second windows holding a frame
above 10 ms, clustered while walking in meadows. Nothing here has been measured in-game;
every cost claim below is a mechanism claim, not a measurement.

## 1. What the late update does (verified by reading)

`ClutterSystem.LateUpdate` runs once per rendered frame. It bails out and calls `ClearAll`
when the overworld render group is inactive, resolves the main camera, picks the player
position as the centre, then calls `UpdateGrass` only when `IsHeightmapReady` is true.
`IsHeightmapReady` calls `Heightmap.HaveQueuedRebuild(cameraPos, m_distance)`, so the whole
clutter pass is skipped while any heightmap within the radius still has a queued rebuild.
The tail of the method writes `_PlayerPosition` and `_PlayerOldPosition` shader globals.

`UpdateGrass` calls `GeneratePatches` then `TimeoutPatches`, and does nothing at all when
vegetation quality is `Off`. `GeneratePatches` walks the patch grid outward from the player's
own patch in square rings. With the shipped defaults `m_grassPatchSize = 8` and
`m_distance = 40`, the ring count is `CeilToInt((40 - 4) / 8) = 5`, so it makes 121
`GeneratePatch` calls every frame. That per-frame sweep is cheap: each call is a distance
test plus a dictionary lookup, and for a live patch only resets `m_timer` to zero.

`GeneratePatch` is where work is admitted, and it already carries a one-per-frame throttle:
`if (!rebuildAll && generated && !m_menuHack) return;`. In the normal path at most one
`GenerateVegPatch` runs per frame. `TimeoutPatches` destroys any patch whose timer passed
2 s, which is how patches behind a walking player are reclaimed.

`GenerateVegPatch` is the expensive call. Per patch it calls `Heightmap.FindBiomeClutter`
four times through `GetPatchBiomes` (each one a linear scan of the loaded heightmap list),
then loops over every entry in `m_clutter` whose biome mask intersects the patch. For each
entry it draws `m_amount` candidate points, scaled by quality (`Low` = /4, `Med` = /2) and by
`m_amountScale`. Each candidate can run `WorldGenerator.GetForestFactor`, `Utils.Fbm` with
three octaves, and then `GetGroundInfo`, which is a `Physics.Raycast` from 500 m above the
point straight down over 1000 m against the terrain layer mask. Survivors additionally read
`hmap.GetOceanDepth`, `hmap.GetVegetationMask` and `hmap.IsCleared`. Placement ends either in
`InstanceRenderer.AddInstance` (one `Instantiate` of the prefab per clutter entry per patch,
instances appended into a 1023-slot matrix array) or, for non-instanced entries, one
`Instantiate` per accepted candidate.

So the dominant per-patch cost is `Physics.Raycast`, once per candidate point, times the
number of clutter entries active in the biome, plus `Instantiate`. `AddInstance` itself is a
`Matrix4x4.TRS` and an array store, and is not the problem.

Two ways a single frame can reach tens of milliseconds:

- **One expensive patch.** The `generated` throttle admits one patch, but that one patch pays
  `entries x amount` raycasts. This is the explanation that fits walking in open meadows.
- **A forced rebuild.** `m_forceRebuild` makes `rebuildAll` true, which bypasses the throttle
  entirely and lets all 121 ring positions generate in one frame. It is set by `ClearAll`
  (render group inactive, or a vegetation quality change) and by `ResetGrass`, which is
  called only from `TerrainComp` and `TerrainModifier`, that is from terrain modification,
  not from walking.

The capture cannot currently tell these apart. That is what the telemetry in section 4 is for.

## 2. Is it main-thread bound

Partly. `Physics.Raycast`, `UnityEngine.Object.Instantiate` and `Destroy` are main-thread-only
Unity APIs and cannot move to a worker thread as written. `Physics.Raycast` does have a
batched job-friendly counterpart, `RaycastCommand`, which schedules across worker threads;
the results still have to be joined on the main thread, but the scan itself parallelises.
`GetForestFactor` and `Utils.Fbm` are pure math and are thread-safe in principle.

Time-slicing across frames is already in the game at patch granularity: one new patch per
frame, generated nearest-first because the ring walk starts at the player's patch. Adding a
coarser budget (one patch every N frames) would lower the *frequency* of heavy frames but
not their *height*, because the peak is one indivisible patch. What the player would see is
grass filling in later at the outer edge of the 40 m radius while moving fast, and, if the
budget is too tight, a visible ring of missing clutter that follows the player.

Slicing *inside* a patch is the only thing that lowers the peak, and it is unsafe as a naive
change: `GeneratePatch` inserts the finished `PatchData` into `m_patches`, and any patch in
that dictionary is treated as complete forever until its 2 s timer expires. A half-filled
patch inserted early becomes permanently sparse grass.

## 3. Candidate bounded modules

**A. Batched ground queries (recommended).** Harmony prefix on
`ClutterSystem.GenerateVegPatch` that reproduces the candidate-point generation with the same
`Random.InitState` seeds, resolves every ground query in one `RaycastCommand` batch, then runs
the acceptance loop reading from that batch. Invariant: same rays, same hits, same placements,
so the player sees nothing change. Risks: the seed reproduction must match the native loop
exactly or every blade of grass moves; `RaycastCommand` does not return the collider component
directly in older APIs, so `GetComponent<Heightmap>` handling needs care; the prefix fully
replaces a large native method and is fragile across game updates.

**B. Cheaper ground queries.** Harmony prefix on the public `ClutterSystem.GetGroundInfo`
replacing the 1000 m raycast with a heightmap lookup (`Heightmap.FindHeightmap` plus
`GetWorldHeight`, normal from finite differences). Much smaller patch surface, much larger
behavioural risk: heightmap interpolation and collider mesh do not agree exactly, so clutter
would sit at slightly different heights and tilts than vanilla. This changes what the player
sees, at the millimetre-to-centimetre scale. Not recommended without a visual A/B.

**C. Frame budget on patch admission.** Harmony prefix on `ClutterSystem.GeneratePatches`
that returns without generating when the previous frames' clutter cost exceeded a configured
budget, never suppressing a `rebuildAll` pass. Tiny, reversible, and cannot corrupt patch
state because it only declines to start work. It smooths clusters of heavy frames but does
not reduce the 59.8 ms peak. Cheapest thing to ship first.

Multiplayer: clutter is client-local. The whole file allocates plain
`UnityEngine.Object.Instantiate` objects under a local `grassroot` transform, keeps them in a
private dictionary, and touches no `ZNetView`, `ZDO`, `ZRoutedRpc` or `ZNetScene`. No module
above can desynchronise a server. Memory is bounded by the existing patch pool
(`AllocatePatch`/`FreePatch`) and the 2 s timeout, and none of the designs change that.

## 4. Telemetry to add before optimizing (count-only)

Counters, no new timers, exported alongside the existing heightmap regeneration attribution:

1. `clutter.patches_generated` — increments in `GenerateVegPatch`. Divided by the frame count
   this says whether heavy frames are one patch or many.
2. `clutter.rebuild_all_frames` — increments when `UpdateGrass` is entered with
   `rebuildAll` true. Settles hypothesis one against hypothesis two directly.
3. `clutter.ground_queries` — increments in `GetGroundInfo`. Per generated patch this gives
   the raycast count that the cost model above assumes.
4. `clutter.instantiated` — objects created per patch, separating `Instantiate` cost from
   raycast cost.
5. `clutter.heightmap_not_ready_frames` — increments when `IsHeightmapReady` returns false,
   to show how much of the walking window skips the pass entirely.
6. `clutter.patches_timed_out` — from `TimeoutPatches`, to confirm churn rate while walking.

Follow the existing probe conventions: prefix/finalizer pairs registered like the other
timing probes, availability reported per probe, counters as plain `long` fields like the
terrain coalescing module keeps.

## 5. Verdict

Feasible, with the gain concentrated in design A. If a heavy frame is one patch of roughly
one to two thousand raycasts, batching them across worker threads should remove most of a
20 to 60 ms frame, plausibly leaving 5 to 15 ms of main-thread join and `Instantiate` work.
That number is a model, not a measurement, and it collapses if the real cause is forced
rebuilds instead. Design C is low risk and can ship first but buys smoothing only, not peak
reduction. Design B is not worth its visual risk.

Total clutter cost is 0.44 % of wall, so this is a stutter problem, not a throughput problem.
Ship the counters first; they cost nothing and they decide which design is even relevant.

## Verified by reading vs assumed

Verified: the LateUpdate flow and its readiness gate; the 121-call ring sweep and its
derivation from `m_grassPatchSize = 8` and `m_distance = 40`; the one-patch-per-frame
`generated` throttle and its `rebuildAll` bypass; `m_forceRebuild` being set only by
`ClearAll` and `ResetGrass`; `ResetGrass` being called only from `TerrainComp` and
`TerrainModifier`; the per-candidate `Physics.Raycast` in `GetGroundInfo`; the four
`FindBiomeClutter` calls per patch and `FindHeightmap` being a linear scan; the quality
divisors; the 2 s timeout; `AddInstance` being trivial and capped at 1023; and that
`ClutterSystem` touches no networking type.

Assumed: how many clutter entries are active in meadows and their `m_amount` values, which
live in game assets and not in code; the per-raycast cost; that the observed spikes are
single-patch generation rather than forced rebuilds; and every millisecond figure in
section 5.
