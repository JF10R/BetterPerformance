# Hourly asset unload deferral

`[Memory] DeferHourlyAssetUnloadEnabled` (off by default), `MaxDeferMinutes` (120, range 60-720). Both roles.

## What vanilla does

`Game.Start` schedules `CollectResourcesCheckPeriodic` with `InvokeRepeating(..., 3600, 3600)`. When the last unload is over 3,599 s old, it calls `CollectResources`, which runs `Resources.UnloadUnusedAssets()`. The work shows up as a loop gap with no instrumented method. On 2026-09-23 it cost 306 ms on the dedicated server, 332 ms and 167 ms on the two clients, each at its own hour mark.

Vanilla already unloads at calm moments through `CollectResourcesCheck` (1,200 s threshold): 5 s into sleep, at respawn, and after 5 s without input in the pause menu.

## What the module changes

A prefix on `CollectResourcesCheckPeriodic` decides (`AssetUnloadPolicy`):

- last unload ≤ 3,599 s old: vanilla runs and only logs a skip;
- empty dedicated server: vanilla runs;
- deferred for `MaxDeferMinutes`: vanilla runs. The deferral counts from the last unload or, on a server, from when players arrived if that is later: an unload done on the empty server before play would otherwise bring the cap into the session (2026-09-24: empty-server unload 17:39, players 18:02, capped unload in play 19:39);
- client: skip. The next sleep, respawn or idle pause unloads through the native check;
- dedicated server with a peer connected or joining: skip, then unload from `Plugin.Update` (checked every 5 s) once `GetPeers()` is empty.

Nothing is dropped. The unload itself is unchanged.

## Gauges

`asset_unload_deferred`, `asset_unload_capped`, `asset_unload_idle_runs`, `asset_unload_native_runs`, `asset_unload_pending`, `asset_unload_since_last_s`, `asset_unload_since_play_s` (server, while peers are connected); label `asset_unload_status`. Each unload still prints the native `Unloading unused assets` log line.

## Limits

A client that never sleeps, respawns or idles in the menu for two hours still gets the capped unload mid-play. Memory held by unused assets grows until the next unload. The cap bounds that.
