# Hourly asset unload deferral

`[Memory] DeferHourlyAssetUnloadEnabled` (off by default), `MaxDeferMinutes` (120, range 60-720). Both roles. `UnloadDuringTeleport` (default on, client, since 0.4.18).

## What vanilla does

`Game.Start` schedules `CollectResourcesCheckPeriodic` with `InvokeRepeating(..., 3600, 3600)`. When the last unload is over 3,599 s old, it calls `CollectResources`, which runs `Resources.UnloadUnusedAssets()`. The work shows up as a loop gap with no instrumented method. On 2026-09-23 it cost 306 ms on the dedicated server, 332 ms and 167 ms on the two clients, each at its own hour mark.

Vanilla already unloads at calm moments through `CollectResourcesCheck` (1,200 s threshold): 5 s into sleep, at respawn, and after 5 s without input in the pause menu.

## What the module changes

A prefix on `CollectResourcesCheckPeriodic` decides (`AssetUnloadPolicy`):

- last unload ≤ 3,599 s old: vanilla runs and only logs a skip;
- empty dedicated server: vanilla runs;
- deferred for `MaxDeferMinutes`: vanilla runs. The deferral counts from the last unload or, on a server, from when players arrived if that is later: an unload done on the empty server before play would otherwise bring the cap into the session (2026-09-24: empty-server unload 17:39, players 18:02, capped unload in play 19:39);
- client: skip. The next sleep, respawn, idle pause or distant teleport unloads through the native check;
- dedicated server with a peer connected or joining: skip, then unload from `Plugin.Update` (checked every 5 s) once `GetPeers()` is empty.

Nothing is dropped. The unload itself is unchanged.

### Distant teleports (0.4.18)

Vanilla never checks during a teleport, and on 2026-09-25 two players went 2 h without sleeping or dying: the cap forced the unload in play on both clients (197 and 319 ms), 1-2 min before their next portal. A prefix on `Player.UpdateTeleport` now calls the native `CollectResourcesCheck` (1,200 s threshold) once per distant teleport, after the move (2 s) and once `ZNetScene.IsAreaReady` holds at the destination. The old area's objects are gone by then, and the frame runs before the native call can end the teleport, so the freeze lands behind the loading screen.

Running the unload more often in play is not expected to shrink it: Unity scans every loaded object. The costs seen so far track the machine more than the interval (2026-09-24/25: 143 and 197 ms on one client, 309 and 319 ms on the other); two sessions are thin evidence.

## Gauges

`asset_unload_deferred`, `asset_unload_capped`, `asset_unload_teleport_runs`, `asset_unload_teleport_ms_max`, `asset_unload_idle_runs`, `asset_unload_native_runs`, `asset_unload_pending`, `asset_unload_since_last_s`, `asset_unload_since_play_s` (server, while peers are connected); label `asset_unload_status`. Each unload still prints the native `Unloading unused assets` log line.

## Limits

A client that never sleeps, respawns, idles in the menu or takes a distant teleport for two hours still gets the capped unload mid-play. Memory held by unused assets grows until the next unload. The cap bounds that.
