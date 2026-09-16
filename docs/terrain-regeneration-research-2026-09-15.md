# Terrain regeneration research (2026-09-15)

## Verdict

The terraforming stall is **not** dominated by heightmap regeneration. Regeneration averages 4.6 ms and peaks at 8.4 ms, while a single burst frame reached 99.7 ms with 10 routed operations summing 60 ms. The operation itself is the larger term, and inside it the dominant repeated work is `TerrainComp.Save`: a full serialization of two 4225-entry arrays plus `Utils.Compress`, re-run **once per painted zone-edge vertex** through the `spread()` local function in `PaintCleared`. The highest-value candidate is coalescing those neighbour saves to one per neighbour per operation. It touches no collision geometry and leaves the saved bytes identical.

Of the 4.6 ms regeneration, mesh rebuild is 4.4 ms (collision 2.1, render 2.3). Everything else, including `ApplyModifiers` over ~100 legacy modifiers, is about 0.2 ms. Legacy-modifier migration (`optterrain`) is therefore worth at most ~4 % of a regeneration and is not a performance lever here.

## Pipeline (VERIFIED, decompiled 1.0.12)

1. `TerrainOp.cs` `Awake`: `Heightmap.FindHeightmap(base.transform.position, GetRadius(), list)`, then per heightmap `item.GetAndCreateTerrainCompiler().ApplyOperation(this)`. **One player action fans out to every heightmap within the radius** — 2 at a zone edge, 4 at a corner. `IsPointInside(point, radius)` is an inflated-AABB test, so 372 routed RPCs is an upper bound on player actions, not a count of them.
2. `TerrainComp.cs` `ApplyOperation`: `m_nview.InvokeRPC("RPC_ApplyOperation", zPackage)`. `RPC_ApplyOperation` runs the body only `if (m_nview.IsOwner())`.
3. `TerrainComp.cs` `DoOperation`: `InternalDoOperation(...)`, then `Save(paintOnly)`, then `m_hmap.Poke(1, paintOnly)`, then `ClutterSystem.instance.ResetGrass(pos, modifier.GetRadius())`.
4. `Heightmap.cs` `Poke`: `delayed > 0` sets `m_doLateUpdate = delayed`; otherwise it calls `Regenerate()` inline. `delayed == 1` is drained by Heightmap's **own** Unity `LateUpdate` (`if (m_doLateUpdate == 1)`), `delayed == 2` by `CustomLateUpdate` (`if (m_doLateUpdate == 2)`).
5. `Heightmap.cs` `Regenerate`: `Generate()` (`RequestTerrainSync` only when `m_buildData` is stale, copy base heights, `m_paintMask.SetPixels`, `ApplyModifiers`), then when `m_regenRequest == RegenRequest.Full`: `RebuildCollisionMesh()`, `UpdateCornerDepths()`, shader texture assign, `RebuildRenderMesh()`, `m_clearConnectedWearNTearCache?.Invoke()`.

**Coalescing is already present.** `m_doLateUpdate` is a single int, so N pokes on one heightmap in one frame collapse to one `Regenerate`. `TrySetPaintOnlyRequest` escalates `PaintOnly` to `Full` within the frame and re-arms per `Time.frameCount`. There is no coalescing win left on this path. Two paths bypass it and regenerate inline: `CheckLoad` (`m_hmap.Poke()`, no argument) called from `TerrainComp.Update` every frame on every instance whenever `ZDO.DataRevision` moved, and `TerrainModifier.PokeHeightmaps` with `m_triggerOnPlaced` set.

**Why ~1900 regenerations for 372 operations.** `Heightmap.OnEnable` calls `Regenerate()` unconditionally for non-LOD maps, and `ZoneSystem.SpawnZone` does `UnityEngine.Object.Instantiate(m_zonePrefab, ...)` for `SpawnMode.Ghost` as well as `Full` — a ghost zone pays a complete regeneration including render mesh and collider cook, then `UnityEngine.Object.Destroy(root)`. Exploration, not terraforming, is the bulk of the 1,898. Collision and render counts (1,897 each) being 1:1 with regeneration confirms essentially every request was `Full`; the paint-only path was taken once.

## Where the operation cost is (VERIFIED code, cost attribution INFERRED)

`TerrainComp.Save` writes `m_modifiedHeight.Length` (4225) bool entries plus two floats where set, then 4225 paint entries plus four floats where set, then `Utils.Compress(zPackage.GetArray())`, then `m_nview.GetZDO().Set(ZDOVars.s_TCData, bytes)`. For `paintOnly` it first runs `ComputePaintMaskHash`, a full 4225-entry walk, as an early-out.

`PaintCleared` iterates the paint kernel and, for every vertex with `x3 == 0`, `x3 == m_width`, `y3 == 0` or `y3 == m_width`, calls `spread(...)`, whose body is:

```
neighbor.m_modifiedPaint[num13] = true;
neighbor.m_paintMask[num13] = color2;
neighbor.Save(paintOnly);
neighbor.m_hmap.Poke(1, paintOnly);
```

A full serialize-and-compress per edge texel, and three `spread` calls per corner texel. A radius-2 hoe stroke along a zone border produces roughly 5 to 15 complete neighbour saves in one operation. This is the strongest available explanation for a 20 ms `RPC_ApplyOperation`; it is an inference from code shape, not yet measured, because no save-level counter exists.

`HeightmapLateBatch` (747 k calls, 39 ms max) does **not** contain terraforming regenerations. Those run at `m_doLateUpdate == 1` in Heightmap's own `LateUpdate`, outside `MonoUpdaters.LateUpdate.Heightmap`. The 39 ms peak in that batch is the `Poke(2)` population, i.e. `TerrainModifier.PokeHeightmaps(forcedDelay: true)` on zone load. Treat the two as separate budgets.

ValheimPlus applies **no** Harmony patch to `Heightmap` or `TerrainComp`; the only references in the decompiled `vplus/` tree are `AEM.cs` (`HitHeightmap` field) and `FreePlacementRotation.cs` (an out parameter). It is not a confound here.

## Prior art

| Source | What it does | Relevance |
| --- | --- | --- |
| VCP `AsyncColliderBakePatch.cs` (in `.qa/references/vcp-frontier/`) | `Physics.BakeMesh` on a ThreadPool worker; hides `m_collider` in a prefix so vanilla skips the cook, assigns in a `MonoUpdaters.LateUpdate` postfix | **Deliberately excludes terraforming**, fresh generation and the player's own zone. Only `SpawnMode.Client` spawns and already-deferred `TerrainModifier` pokes qualify. Drains pending bakes before `ForceGenerateAll` and in `OnDestroy`. |
| VCP `TerrainOpPaintFanoutPatch.cs` | Extends the `TerrainOp.Awake` fan-out to the -x/-z zones the floored paint kernel actually reaches | Confirms fan-out is per-operation and already load-bearing for correctness. Any coalescing change must not shrink it. |
| VCP `ClutterRebuildCapPatch.cs` | Caps `ClutterSystem` whole-area rebuilds, citing `TerrainComp.CheckLoad` triggering ~64 patches at hundreds of raycasts in one frame | The grass side of the same stall; already solved upstream. |
| VPO (ontrigger) | "Experimental threaded terrain collision baking", config-gated | Reported failure mode is terrain visually disappearing; the project's own guidance is to disable the option. REPORTED, not verified. |
| `optterrain` (Iron Gate, 0.150.3) | Converts legacy `TerrainModifier` instances to `TerrainComp` data | Runs per area, is a world-data rewrite, and community reports include warped buildings. REPORTED. |
| BetterTerrain (74oshua) | Replaces the modifier pipeline and stores heightmaps in a side `.hmap` file | Rewrites save data. Out of scope under the repo's data-safety rules. REPORTED. |

## Ranked candidates

| # | Candidate | Mechanism | Invariants that must hold | Expected gain | Risk |
| --- | --- | --- | --- | --- | --- |
| 1 | Coalesce `spread()` neighbour saves | Collect touched neighbours in a set during `PaintCleared`; `Save` + `Poke(1)` each once after the kernel loop | Final `m_paintMask` / `m_modifiedPaint` byte-identical; same neighbours poked in the same frame; fan-out unchanged | Removes N-1 of N serialize+compress per border operation; plausibly tens of ms on a 10-operation burst frame | Low. No geometry, no collision, no wire-format change. Needs care if another patch reads the neighbour ZDO mid-operation |
| 2 | Suppress `RebuildRenderMesh` for ghost zones | Context flag around `ZoneSystem.SpawnZone` with `SpawnMode.Ghost`; skip render mesh and the wear-cache invalidation only | Collision and heights untouched (`PlaceVegetation` / `GetGroundData` raycast against the collider); root is destroyed regardless | ~2.3 ms per ghost zone spawn, on the exploration path | Low, but verify no code reads `m_meshFilter.mesh` during placement |
| 3 | Defer `RebuildRenderMesh` one frame on the terraform path | Keep collision and corner depths immediate; queue the render mesh | Collision current in the same frame; render mesh current within one frame; `ForceGenerateAll` drains it | ~2.3 ms per regeneration in a burst frame | Low-medium. One frame of stale shading and normals |
| 4 | Coalesce `CheckLoad` to `Poke(1)` | Change the inline `m_hmap.Poke()` to a delayed poke so several revisions in one frame collapse | `ClutterSystem.ResetGrass` still fires; `HaveQueuedRebuild` semantics respected | Mostly the second player's client; small on the host | Low |
| 5 | Async `Physics.BakeMesh` on the terraform path | Extend VCP's pattern past its own exclusion | Player standing on edited ground, dropped items, the `ItemDrop` that `TerrainOp.OnPlaced` spawns with upward velocity, and carts all need collision this frame | ≤2.1 ms per regeneration | **High.** Worst gain-to-risk ratio here; VCP excluded exactly this case on purpose |
| 6 | Legacy modifier migration | `optterrain`-equivalent | World data rewrite | ≤0.2 ms per regeneration, ~4 % | Not worth it. Recommend dropping |

## Falsification

Disposable world, scripted hoe operations, existing `HeightmapRegenerate` / `HeightmapCollisionRebuild` / `HeightmapRenderRebuild` / `LoopInterval` metrics plus the new counters below.

- **Candidate 1.** Two matched strokes of equal operation count: one entirely inside a zone, one along a zone border. If the border stroke does not show materially higher `RPC_ApplyOperation` elapsed and `TerrainCompSave` count, the `spread()` hypothesis is falsified and candidate 1 should be dropped. With the patch applied, saved bytes must be byte-identical before and after for the same stroke; any difference fails.
- **Candidate 2.** Fly a fixed route generating fresh zones, with and without the suppression. `HeightmapRenderRebuild` count must fall by the ghost-zone count while `HeightmapCollisionRebuild` is unchanged. Any vegetation or location misplacement fails it.
- **Candidate 3.** Repeated hoe on a slope while watching for visible terrain tearing, and a ship or cart driven over freshly raised ground. Any object landing on stale geometry fails it.
- **Candidate 5.** Level ground out from under the player, drop items onto a freshly raised platform, and cross with a cart. Any fall-through, float, or lost item fails it outright.

Reject any candidate whose `LoopInterval` maximum during a matched burst does not improve beyond the run-to-run spread of two baseline captures.

## What not to do

Do not throttle `Regenerate` behind a global frame budget. `ClutterSystem.IsHeightmapReady` gates on `Heightmap.HaveQueuedRebuild`, and `Ship`, `Vagon` and `SnapToGround` call `Heightmap.ForceGenerateAll` expecting current collision; a budget that leaves the queue undrained stalls grass and puts vehicles on stale ground. Do not skip `UpdateCornerDepths`; it feeds the `_depth` shader array. Do not change the `s_TCData` package layout or add a side file. Do not shrink the `TerrainOp.Awake` fan-out as a cost saving; VCP had to widen it for paint correctness.

## Telemetry to add

- `TerrainCompSave`: count, elapsed and compressed byte size, tagged `direct` versus `spread`. This is the missing measurement for candidate 1 and should land before any patch.
- `TerrainCompPaintHash`: elapsed for `ComputePaintMaskHash`, to price the paint-only early-out.
- `TerrainOpFanout`: heightmaps per operation, from the `FindHeightmap` list count in `TerrainOp.Awake`.
- `HeightmapRegenerate` reason tag: `terrain_op` (`m_doLateUpdate == 1`), `zone_spawn` (`OnEnable`), `data_revision` (`CheckLoad`), `modifier` (`m_doLateUpdate == 2`), `force` (`ForceGenerateAll`). Without it the 1,898 total cannot be split between exploration and terraforming.
- Regenerations-per-frame histogram, to test whether 50-100 ms frames are many heightmaps or many operations. Current evidence points at operations.
- `ClutterResetGrass` count and radius, since `CheckLoad` passes a whole-zone radius.

Document `HeightmapLateBatch` as excluding the terraform path in `docs/base-simulation-telemetry.md`; the current wording invites the opposite reading.

## Sources

Decompiled Valheim 1.0.12: `Heightmap.cs`, `TerrainComp.cs`, `TerrainOp.cs`, `TerrainModifier.cs`, `ZoneSystem.cs`, `ClutterSystem.cs`, `MonoUpdaters.cs`. In-repo prior art: `.qa/references/vcp-frontier/ValheimCommunityPatch/Patches/`.

- [Unity Physics.BakeMesh](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.BakeMesh.html)
- [Valheim Performance Optimizations (Nexus)](https://www.nexusmods.com/valheim/mods/1360)
- [ValheimPerformanceOptimizations (DeepWiki)](https://deepwiki.com/ontrigger/ValheimPerformanceOptimizations)
- [Valheim Community Patch](https://github.com/MidnightsFX/Valheim-Community-Patch)
- [optterrain command reference](https://valheimcheats.com/command/optterrain)
- [Valheim 0.150.3 terrain system change](https://techraptor.net/gaming/news/valheim-patch-01503-released-includes-new-terrain-modification-system)
- [BetterTerrain README](https://github.com/74oshua/BetterTerrain/blob/master/README.md)
