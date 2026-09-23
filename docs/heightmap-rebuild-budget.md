# Heightmap rebuild budget

One opt-in client module, default off (`[Terrain] RebuildBudgetEnabled`). It spreads the
terrain rebuilds vanilla queues for the late update across frames, nearest and in view
first. It drops no rebuild and changes no terrain data: a deferred rebuild stays queued and
vanilla runs it on a later frame.

## Why

`TerrainModifier` (location levelling and paint) queues a rebuild with `Heightmap.Poke(2)`
on every overlapping heightmap when it is created or destroyed, so a zone with locations
loading or unloading queues many at once. `Heightmap.CustomLateUpdate` then runs every one
in the same frame, with no per-frame bound. In the 2026-09-23 captures the heightmap late
batch was the top attributed cause of loops of 100 ms or more: 18 of 45 on the second
player's client, 5 of 15 on ours, max 134 ms, with 10 to 128 rebuilds of 4-8 ms each in one
frame.

## Mechanism

A Harmony prefix on `Heightmap.CustomLateUpdate`. On the first queued heightmap of a frame
it plans over every heightmap in `Heightmap.Instances` whose `m_doLateUpdate` is 2:

- **Critical, always run:** the heightmap square is within `RebuildCriticalRadius` of the
  local player, or within the larger of that radius and the grass distance
  (`ClutterSystem.m_distance`) of the camera. Same square test vanilla uses (`IsPointInside`).
- **Overdue, always run:** first seen queued at least `RebuildMaxDeferMilliseconds` ago.
- **The rest:** by effective distance from the camera — ×1 within 60° of the view, ×1.3 in
  the front half, ×2 behind — while the projected spend fits the allowance, and at least one
  per frame. The allowance is `RebuildBudgetMilliseconds`, raised when needed so the queue
  drains before its oldest entry turns overdue (otherwise a burst would move, whole, to its
  deadline frame). The projection uses a moving average of the measured rebuild cost; the
  measured spend can still stop an optional rebuild mid-frame.

With 0 or 1 queued rebuilds nothing is planned. A deferred heightmap's prefix returns false,
which skips only `CustomLateUpdate`, whose single call is `Regenerate`.

## Why a deferral is safe

Checked against every reader and writer of the queue flag in the game assembly; the
contract test fails if that set changes.

- Only `TerrainModifier.PokeHeightmaps` queues (flag 2). Player digging, levelling and
  painting use `Poke(1)` (Unity `LateUpdate`) or `Poke(0)` (immediate): not touched.
- A zone's first build is `Regenerate` inside `Heightmap.OnEnable`, never the queue.
- `ForceGenerateAll` regenerates everything still queued; `SnapToGround.SnappAll` (location
  and dungeon spawn), `Ship.Awake` and `Vagon.Awake` call it, so they never see a deferred
  collider.
- `ClutterSystem` withholds grass while a queued heightmap is within `m_distance` of the
  camera; those heightmaps are critical, so grass is never stalled by a deferral.
- A request is never lost: the flag stays 2, and while queued `m_regenRequest` can only stay
  or rise to `Full`.
- Distant-LOD heightmaps are never queued natively (`PokeHeightmaps` iterates
  `GetAllHeightmaps`, which excludes them); if one were, it runs.
- A heightmap destroyed while deferred leaves `Heightmap.Instances` and the plan, as vanilla.
- Not installed on a headless process (dedicated server); no deferral without a `ZNet`, a
  camera, or on a dedicated `ZNet`.

**Not covered:** a physics body outside the critical radius stands on the pre-rebuild
collider of its heightmap for up to `RebuildMaxDeferMilliseconds` (vanilla: until the same
frame's late update). The 80 m default covers the native creature spawn ring (40-80 m).

## Configuration

| Key (`[Terrain]`) | Default | Range |
|---|---|---|
| `RebuildBudgetEnabled` | false | restart required |
| `RebuildBudgetMilliseconds` | 4 | 1-50 |
| `RebuildCriticalRadius` | 80 m | 32-512 |
| `RebuildMaxDeferMilliseconds` | 500 | 50-5000 |

Estimated, not measured: the pacing spreads the worst burst observed (134 ms of rebuilds)
over frames carrying about 6 ms each at 500 ms, 18 ms at 250 (16 ms base frame).

## Gauges

Per interval: `heightmap_budget_rebuilds_run`, `_deferred` (decisions, including
`_demoted_measured`), `_overdue_forced`, `_critical`, `_budgeted`, `_planned_frames`,
`_frames_over_budget` (measured spend above `RebuildBudgetMilliseconds`),
`_max_deferral_ms`, `_frame_spend_max_ms`, `_plan_ms_max`, `_queue_peak`,
`_cost_estimate_ms`, `_probe_failures`. Labels: `heightmap_budget_status`, `_enabled`, `_ms`.
