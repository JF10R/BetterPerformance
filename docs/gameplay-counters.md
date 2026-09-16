# Gameplay counters

Counts and sizes for the gameplay surfaces a play session touches: chests, inventories, stations, building, gathering, combat, the map, vehicles and item drops. Nothing here times anything; elapsed time at the same call sites is exported as separate timing metrics and rendered in the same report section. Every hook is a count-only prefix or postfix that cannot skip a native call or replace its result or exception.

Bound to `[Diagnostics] GameplayCountersEnabled` (default true, restart required).

## What a counter means

A counter is observed native calls, not coverage of the activity it names. A zero means nothing was observed in the retained windows, never that nothing happened. Each probe installs independently: a changed game signature disables only its own counter and is listed in `gameplay_probes_unavailable`, and the module status becomes `partial`.

- `container_concurrent_open_conflicts` counts `Container.RPC_RequestOpen` arrivals where the container already reported `IsInUse()`. It is counted on the machine that owns the container, so one capture sees only its own share. `container_open_requests_rejected_in_use` is the matching refusal seen by the requester, and `container_open_granted` the acceptance; a requester and an owner in the same session count different events.
- `smelter_catchup_items_sum` and `_max` are the drop in queued plus processed items across one `Smelter.UpdateSmelter`. That call catches up one accumulated game hour at a time, so a large value is deferred work being drained, not work created in that frame.
- `minimap_fog_applies` counts `Minimap.Explore(Vector3,float)` scopes in which at least one pixel became newly explored, which is exactly the native condition for applying the fog texture. `minimap_fog_pixels_explored` counts those pixels.
- `drops_spawned` sums the length of each `DropTable.GetDropList()` result: objects the table selected, not objects confirmed instantiated. `drop_on_destroyed_events` counts the destruction events that asked for a list.
- `pieces_removed` and `rock_area_destroys` count only calls that returned true.

## Frame gauges

One per-frame observation runs, as a postfix on `Hud.Update`, and reads a handful of statics with no allocation: GUI visibility, the shown container, the map mode and whether the local player is attached to a ship. `gameplay_observed_frames` is the denominator for `container_gui_open_frames`, `inventory_gui_open_frames`, `minimap_large_map_frames` and `player_on_ship_frames`. It counts frames in which that native method ran, so it is not a frame-rate measurement and is not comparable to a render frame count.

`placement_ghost_frames` is separate: it counts `Player.UpdatePlacementGhost` calls that left a non-null ghost, which is a build-mode call rate, not a frame count.

## Sizes read at poll time

`inventory_items_max`, `ship_instances_max`, `vagon_instances_max`, `zsfx_instances_max` and `smelter_catchup_items_max` are read when the capture samples. They are occupancy, not throughput, and a peak between two polls is invisible, so the reported range is a lower bound. The inventory size is `Inventory.NrOfItems()`, a list count and not a stack total.

## Not counted

- `container_in_use_max` and `smelter_instances_max`: the game keeps no static instance list for `Container` or `Smelter`, and enumerating the scene to build one is not a bounded read.
- `audio_sources_playing`: no cheap bounded read exists for it.

These are listed in the `gameplay_skipped_counters` label so a capture states its own gaps.

## Bounds, coverage and privacy

The exported state is a fixed set of scalars. Nothing per-object is retained, no object, player, container, prefab or world name is read or exported, and no coordinates leave a hook. Capture start binds the counters to the current main thread; callbacks on other threads are skipped and counted in `gameplay_other_thread_skips`, and an observer exception is counted in `gameplay_probe_failures`. Skipped observations are unmeasured calls, not zero calls.

## Verification

`GameplayGameTests.Run` verifies, against the installed game assemblies and through Mono.Cecil metadata, the exact parameter types, return type and static/instance shape of every hooked method and every field the module reads, that `Attack.Start` still resolves to a single nine-argument overload, that the instance lists are generic lists with an `int Count`, that no hook can skip a call or mutate an argument, that the configuration key defaults to on, and that a reset sample exports exactly the documented gauge and label set at zero. It launches nothing and invokes no game method. Probes that cannot install in a standalone CLR are reported by name; a refusal caused by a changed game signature fails the harness.
