# Terrain neighbour save coalescing

> **Superseded by the game in Valheim 1.0.15 (2026-09-18).** `spread` no longer calls
> `neighbour.Save(paintOnly)`: it marks `m_modifiedPaint`, writes the mask in memory and
> pokes, and a later `Save` serializes the flags. The per-texel write this module removed
> is gone natively, so on 1.0.15 and later `Verify` rejects the shape, the module declines
> to install and vanilla behaviour is retained. The code is kept because it still applies
> to an installation on 1.0.14 or earlier; the switch is set off on both roles here.
> Everything below describes the pre-1.0.15 native shape.

Opt-in, default off: `[Terrain] CoalesceNeighbourSavesEnabled`. Requires a restart.
Attribution telemetry (below) is separate and on by default.

## Mechanism

`TerrainComp.PaintCleared` spreads a painted zone-edge texel into the neighbouring
compiler through a `spread` local function reached from 12 edge and corner cases. Each
call runs `neighbour.Save(paintOnly)`, a full serialize of two 4225-entry arrays plus
`Utils.Compress` and a ZDO write, then `neighbour.m_hmap.Poke(1, paintOnly)`. Only the
last write survives, so the earlier ones are redundant.

A prefix on `PaintCleared` opens a thread-static batch scope. A prefix on
`TerrainComp.Save` runs, while that scope is open and the instance is not the
operation's own compiler, the native guards (`m_initialized`, `IsValid`, `IsOwner`) and
the native paint-hash gate, latches `m_lastHash` exactly as vanilla would, records the
instance, and skips only the serialize/compress/ZDO write. A finalizer on
`PaintCleared` writes each recorded neighbour once, in first-seen order, at depth zero.

The flush calls `Save(false)`. That path skips `ComputePaintMaskHash` and serializes
the identical bytes, then zeroes `m_lastHash`; the value the simulated gate latched is
written back afterwards. `Poke` is never intercepted. `Heightmap` already collapses N
pokes per frame through `m_doLateUpdate`, so passing it through keeps the same
neighbours poked in the same frame.

The operation's own compiler is saved by `DoOperation` after `PaintCleared` returns, so
it is outside the scope. A Save on the scope's own instance is still detected and left
native, counted as `terrain_saves_native_inside_batch`.

## Invariants

- Final `m_paintMask`, `m_modifiedPaint` and the `s_TCData` bytes are unchanged.
- `m_lastHash` ends at the value the native gate would have latched.
- The same neighbours are poked, with the same delay and paint-only flag, in the same frame.
- Fan-out, collision geometry and the package layout are untouched.
- Known divergence: the ZDO `DataRevision` advances once per neighbour per operation
  instead of once per painted edge vertex. That is the saving. A second divergence is
  possible only if the paint hash returns to its pre-operation value after moving away
  during one operation; vanilla would then write the final state and this does not.

## Contract check

Install validates, from IL, that `Save` still computes the paint hash once and gates on
`m_lastHash`, that it targets `ZDOVars.s_TCData` and compresses, that `spread` saves the
neighbour exactly once on a local receiver and pokes `m_hmap` with delay 1 immediately
after, and that `PaintCleared` reaches `spread` and never saves directly. Any mismatch
falls back to vanilla with `terrain_coalesce_status=unavailable`. Where the runtime
cannot open `PaintCleared`'s body the status is `installed_partial_contract`.

## Byte-parity plan (not yet run)

On a disposable world, with the optimization off then on and matched scripted hoe
strokes along a zone border: capture the neighbour ZDO `s_TCData` byte arrays after each
stroke and compare. Any difference fails the change. Pair it with the research
falsification: two matched strokes of equal operation count, one inside a zone and one
on a border, comparing `RPC_ApplyOperation` elapsed and `terrain_op_neighbours_max`. If
the border stroke is not materially more expensive the premise is falsified.

## Counters

`terrain_batches`, `terrain_saves_deferred`, `terrain_saves_flushed`,
`terrain_saves_gate_skipped`, `terrain_saves_native_inside_batch`,
`terrain_coalesce_fallbacks`; labels `terrain_coalesce_status`,
`terrain_coalesce_enabled`, `terrain_coalesce_poke`.

Attribution (always on, `[Diagnostics] TerrainRegenerationTelemetry`): counting only,
never timing. `heightmap_regen_terrain_op`, `_zone_spawn_full`, `_zone_spawn_ghost`,
`_enable`, `_other`; `heightmap_regen_frames_with_1` / `_2_3` / `_4_plus` /
`_max_per_frame`; `terrain_ops_total`, `terrain_op_rpc_dispatches_total`,
`terrain_ops_per_frame_max`, `terrain_op_neighbours_max`, `terrain_op_neighbours_total`;
`heightmap_regen_other_thread_skips`, `heightmap_regen_probe_failures`. Reason
precedence is terrain operation, then zone spawn, then component enable, then other: a
ghost zone enables its heightmap inside `SpawnZone`, and the zone label is the useful
one there.

## What not to do

Do not intercept `Poke`, shrink the `TerrainOp.Awake` fan-out, change the `s_TCData`
layout, or add a side file. Do not skip the flush on an exception: vanilla would already
have written those neighbours.
