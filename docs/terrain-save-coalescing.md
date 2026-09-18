# Terrain: attribution telemetry (and the removed neighbour-save coalescing)

## Neighbour-save coalescing — removed in 0.4.10

The module existed because, up to Valheim 1.0.14, `TerrainComp.PaintCleared` spread a painted
zone-edge texel into the neighbouring compiler through a `spread` local function that ran
`neighbour.Save(paintOnly)` — a full serialize of two 4225-entry arrays, `Utils.Compress` and a
ZDO write — once per painted texel, from 12 edge and corner cases. Only the last write
survived. The module opened a batch scope around `PaintCleared`, replicated the native
paint-hash gate, deferred the neighbour writes and flushed each neighbour once.

Valheim 1.0.15 (2026-09-18) removed the per-texel save natively: `spread` now marks
`m_modifiedPaint`, writes the mask in memory and pokes, and a later `Save` serializes the
flags. The module's own IL contract refused the new shape, so it had already stopped
installing; on an installation that always runs the latest build there was nothing left for it
to do, and 300 lines of IL-shape code guarding a contract no supported build has would only
rot. It was removed with its `[Terrain] CoalesceNeighbourSavesEnabled` key, its
`terrain_batches`/`terrain_saves_*`/`terrain_coalesce_*` counters and its contract test. A
stale key in a config file is ignored. The game-contract test keeps one two-sided check: if a
future build brings the per-texel save back, it fails and names the reason. History:
research in `placement-research-2026-09-17.md`, the 1.0.14 overlap audit and the paint-loss
fix in `validation-0.4.9.md`.

## Attribution telemetry — always on

`[Diagnostics] TerrainRegenerationTelemetry`: counting only, never timing. Every heightmap
regeneration is attributed to a reason with this precedence: terrain operation, then zone
spawn, then component enable, then other — a ghost zone enables its heightmap inside
`SpawnZone`, and the zone label is the useful one there.

Counters: `heightmap_regen_terrain_op`, `_zone_spawn_full`, `_zone_spawn_ghost`, `_enable`,
`_other`; `heightmap_regen_frames_with_1` / `_2_3` / `_4_plus` / `_max_per_frame`;
`terrain_ops_total`, `terrain_op_rpc_dispatches_total`, `terrain_ops_per_frame_max`,
`terrain_op_neighbours_max`, `terrain_op_neighbours_total`; `heightmap_regen_other_thread_skips`,
`heightmap_regen_probe_failures`. Labels: `terrain_regen_scope`, `terrain_telemetry_status`,
`terrain_op_neighbours_semantics`.

The hooks are prefix/postfix pairs that return void and never take a game argument by
reference, so they cannot skip, replace or alter a native call; the contract test asserts that
shape for every hook.
