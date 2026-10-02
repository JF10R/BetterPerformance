# Teleport loading

`[Diagnostics] TeleportLoadingEnabled` (default on, observes only), `[ObjectLoading] UnbudgetedInLoadingScreen` (default on) and `[Teleport] FastArrivalEnabled` (default off; `MinimumSeconds` 3, `SettleSeconds` 0.75, `NearRadius` 80 m). Client. Since 0.4.18.

## What vanilla does

`Player.TeleportTo` sets `m_teleporting`; `Player.UpdateTeleport`, from `FixedUpdate`, then:

- waits 2 s, then moves the player to the target;
- on a distant teleport (portals), keeps the loading screen until **8 s** have passed, `ZNetScene.IsAreaReady` holds (destination zone loaded, every object of the 3×3 zones around it instantiated) and `ZoneSystem.FindFloor` finds ground; after 15 s without ground it drops the player at the solid height.

Meanwhile `ZNetScene.CreateObjects` creates up to 100 objects per frame (10 in play), and only once `ZoneSystem.IsActiveAreaLoaded`.

## What is measured

One record per teleport, exported in the interval where it ends (`TeleportTimeline`):

| Gauge | Meaning |
| --- | --- |
| `teleport_total_ms` | `TeleportTo` accepted → native end |
| `teleport_moved_ms` | → the move (2 s) |
| `teleport_zone_loaded_ms`, `teleport_active_area_ms` | → destination zone, then active area, loaded (polled after the move) |
| `teleport_area_ready_ms` | → `IsAreaReady` at the target |
| `teleport_floor_ms` | → `FindFloor` at the target |
| `teleport_ready_wait_ms` | time between readiness (move, area, floor) and the end: on a portal, what the 8 s floor costs |
| `teleport_frames`, `teleport_frame_max_ms` | frames under the screen, the slowest one |
| `teleport_budget_yields`, `teleport_instances_delta` | object-budget yields, scene objects gained |
| `teleport_distance_m` | straight-line distance |

Also `teleport_started`, `teleport_completed`, `teleport_distant_completed`, `teleport_replaced_incomplete`, labels `teleport_kind`, `teleport_end` (`floor_found` / `no_floor`). A distant teleport also prints one `Teleport loading:` log line. A milestone never reached is left out, not exported as zero. Since 0.4.19, readiness first reached in the very call that ends the teleport is marked at the end; before, it read "never" (7 of 52 portals on 2026-09-26).

### After arrival (0.4.19)

For 10 s after a distant arrival (`PostArrivalWindow`), exported once the window closes, with a `Teleport after arrival:` log line:

| Gauge | Meaning |
| --- | --- |
| `teleport_after_frames`, `teleport_after_slow_frames`, `teleport_after_very_slow_frames` | frames, over 50 ms, over 100 ms |
| `teleport_after_frame_max_ms`, `teleport_after_slow_ms_sum` | slowest frame, time in slow frames |
| `teleport_after_slow_gpu`, `teleport_after_slow_main_thread` | slow frames whose previous-frame GPU or main-thread counter was over 50 ms (approximate: the engine counters lag one frame) |
| `teleport_after_near_pending`, `teleport_after_distant_pending` | objects of the scene's near and distant lists not yet created at arrival (`ZNetScene.CreateObjects` arguments) |
| `teleport_after_near_drained_ms` | when the near list had nothing left to create; absent if not within 10 s |
| `teleport_after_instances_delta` | scene objects gained during the window |

Label `teleport_after_end`: `window_closed`, or `cut_by_teleport` when another teleport started first. This window decides whether holding the screen until the near objects are created would be worth its cost.

## What changes

### Fast arrival

A prefix on `Player.UpdateTeleport` (last among prefixes) checks the destination every 0.1 s, from the move (2 s) until the native floor (8 s). When nothing below holds it, it sets `m_teleportTimer` just past 8 s; vanilla then runs its own `IsAreaReady` and `FindFloor` in the same call and ends the teleport. It never delays one: any unmet check leaves the native floor in place. `TeleportArrivalPolicy` decides, in this order:

| Blocker | Cleared when |
| --- | --- |
| `active_area` | `ZoneSystem.IsActiveAreaLoaded` (every near-simulation zone exists; its heightmap is built synchronously on creation), counted only once `ZNet`'s reference position is within 32 m of the target: on the move frame it still points at the old area |
| `server` | the destination position has been sent to the server during this teleport (`PositionJumpSync` sees every send, native or early, within 32 m of the target); without that hook, 2 s after the move (the native cadence). Label `fast_arrival_server_signal` |
| `objects` | `ZNetScene.IsAreaReady(target)`: every received object of the 3×3 zones instantiated |
| `floor` | `ZoneSystem.FindFloor(target)` |
| `terrain` | no heightmap within `NearRadius` has a queued rebuild (`m_doLateUpdate` 1 or 2) |
| `grass` | every grass patch `ClutterSystem.GeneratePatches` keeps around the player exists (one is generated per frame: ~80 patches, about 2 s at 40 FPS) |
| `dungeon` | no dungeon still being sliced in (`DungeonSpawnSlicing`) |
| `settling` | the number of static objects (pieces, trees, rocks, locations) in those 3×3 zones has not changed for `SettleSeconds`, counted from 0.3 s after that send at the earliest; creatures, item drops, projectiles, ragdolls, fish and birds are ignored |
| `minimum` | `MinimumSeconds` since entering the portal |

Grass: when `grass` is the first unmet check (zones, objects, floor and terrain ready), the module sets `ClutterSystem.m_forceRebuild` once per teleport, the flag `ClearAll` uses. The next `LateUpdate` then builds every patch in one frame (~0.4-0.55 ms each on 2026-09-25, ~80 patches) under the loading screen instead of one per frame in play. Gauge `fast_arrival_grass_prebuilt`.

`IsAreaReady` only covers objects already received: right after the move the server may not have sent the destination yet, which the 8 s floor used to hide. `server` and `settling` are the guard for that. Before 0.4.20 the settle clock started at the move, so a destination the server had not started streaming looked stable: on 2026-09-30 a 3.1 s portal (client on 0.4.18, no early position send) ended when the client had received about 500 new objects; about 2,900 more arrived in the next 5 s, and the house was missing meanwhile.

Settle gauges: `fast_arrival_settle_change_<category>_<near|mid|far>` counts the checks, after the server could answer, in which that category's count changed within 32 m, 32-64 m or beyond of the target (what reset settling); `fast_arrival_settle_dynamic_ignored_ms` is the time the former all-object rule would still have held. After every distant arrival, fast or native, `fast_arrival_late_static_max`/`_sum` count the static objects that still arrived around the destination in the next 5 s (`fast_arrival_late_teleports`, `fast_arrival_late_over_100` for arrivals with 100 or more: the missing-house signature). The count starts at the real end of the screen: a native arrival that vanilla held past 8 s (up to 15 s) recounts first, since 0.4.21; before, objects received after 8 s under the screen counted as late.

Gauges: `fast_arrival_applied`, `fast_arrival_native_floor`, `fast_arrival_saved_ms_max`/`_sum`, `fast_arrival_failures`, `fast_arrival_wait_<blocker>_ms` (time each check held a teleport, applied or not) and `fast_arrival_held_by_<blocker>` for teleports that kept the floor (what was still loading at the last check). Keep ValheimPlus `disableEightSecondTeleport` off: it would end the teleport on `IsAreaReady` and `FindFloor` alone, before the terrain, grass and settle checks.

Limits: shaders and textures first used at the destination can still hitch the first frames after arrival (look for loop gaps 0-2 s after `Teleport loading:`); distant objects keep loading after arrival, as in vanilla.

### Zone preparation

Two `[Teleport]` switches, both leaving zone construction to the game (`TeleportZonePreparation`):

- `PrefetchTerrainEnabled`: on an accepted distant `TeleportTo`, the terrain of the zones nearest the destination is queued on the game's own `HeightmapBuilder` thread with the call `SpawnZone` makes (`IsTerrainReady`), during the 2 s fade. At most 16, the builder's ready-queue size, so no request evicts another. Same data the game would build after the move. Gauges `teleport_prefetch_*`.
- `ZoneBurstEnabled` (`ZoneBurstMilliseconds` 16): vanilla creates at most one zone per `ZoneSystem.Update` pass, every 0.1 s: ~2-2.5 s for the ~21 zones around a destination (measured 2.8 s on 2026-09-26). After the move and under the loading screen, `InitialLoadingOptimization`'s wrapper of that call runs further native passes while each creates a zone, within the allowance, up to 16 a pass. In play the one-zone pacing is unchanged. Gauges `teleport_zone_burst_*`.

### Loading-screen object budget

The object budget (`[ObjectLoading] Enabled`) no longer applies while the game counts a loading screen (no local player, or teleporting). It used to cap creation at 4 ms per frame there, behind a screen that hides those frames. The distant-teleport unload is in [asset-unload-deferral.md](asset-unload-deferral.md).

## Reading it

- `teleport_ready_wait_ms` large and `teleport_area_ready_ms` well under 8,000: the 8 s floor is what you wait for; fast arrival removes it once the checks above pass.
- `teleport_area_ready_ms` near or past 8,000: loading is the limit. `teleport_active_area_ms` late points at terrain/zones; area ready late after it points at object creation or at the server sending the destination.

Not measured: GPU work under the screen.
