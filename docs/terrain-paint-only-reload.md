# Terrain paint-only reload

`[Terrain] PaintOnlyReloadEnabled` (default off). Client and server. Since 0.4.18.

## What vanilla does

A terrain edit (pickaxe, hoe, cultivator) runs on the owner of the zone's `TerrainComp`, which saves the whole zone's edits into its ZDO. Every other peer then sees a new data revision: `TerrainComp.CheckLoad` → `Load` (all height and paint arrays) → `m_hmap.Poke()`, an immediate **Full** regeneration: collision mesh (~2.4 ms) and render mesh (~2.7 ms) on 2026-09-25, 5.3 ms mean on the second player's client.

The owner distinguishes a paint-only edit (`m_paintCleared` without level, raise or smooth: hoe path, cultivator) and pokes it with `Poke(1, paintOnly: true)`, which refreshes the paint texture and skips both meshes. The ZDO does not carry that distinction, so every other peer rebuilds both meshes anyway.

## What the module changes

A prefix on `CheckLoad` copies the three height arrays (`m_modifiedHeight`, `m_levelDelta`, `m_smoothDelta`) when a new revision is about to load. A prefix on `Heightmap.Poke` turns that same `Poke(0, false)` into a paint-only one when the arrays after `Load` are bit-for-bit equal to the copy (`TerrainHeightSnapshot`).

Why the result is identical: `Regenerate` always recomputes the heights and refreshes the paint texture; only the two mesh rebuilds depend on `Full`, and they read heights and biome colours, never paint. Equal height arrays give equal meshes, which is also what the editing player shows. Any difference, a missing array or any other `Poke` call keeps the Full rebuild. Nothing is sent or saved differently.

Gauges: `terrain_reload_paint_only`, `terrain_reload_full`, `terrain_reload_compare_ms_max`, `terrain_paint_only_failures`; label `terrain_paint_only_status`.

## Limits

Height edits (pickaxe, level, raise) keep the Full rebuild: nothing changes for them.
