# Changelog

### 0.4.21

Fixes from an external audit, each checked against the code first. No gameplay decision changes.

- Network compression: once installed, a received frame is always decoded. Before, eight caught exceptions, or the option turned off in game, also stopped decoding while the peer kept sending frames, which then reached the game raw (never observed). A failure now only stops this side offering and compressing. The receive hook reads the frame header in place instead of copying every packet (up to ~200 KB/s per peer). New gauges `net_compress_{compress,decode}_ms` and `_max_ms`.
- Engine markers: the per-marker ring grows from 600 to 4,096 frames, enough for a 10 s export at about 400 fps. On the last 12 client segments, 19.4 % of intervals had overflowed and lost frames silently. New gauge `engine_marker_coverage_percent` (PlayerLoop samples over frames observed), shown in the report.
- The last export of a capture (`capture_end`) now carries the same context as an interval: CPU, GC, memory, host, system and simulation figures, peers, socket queues and scene size. The report's incident and memory tables read it.
- Heightmap rebuild budget: a burst's last frame is folded at the next export instead of the next burst, and a new capture no longer inherits the previous one's pending frame (`heightmap_budget_frame_spend_max_ms`, `_frames_over_budget`).
- Fast portal arrival: when vanilla holds the screen past 8 s, the late-object baseline is recounted at the real end, so objects received after 8 s are no longer counted as late.
- With `Capture.Enabled = false`, the network, ownership-expedite and sector-fix optimizations now still install when enabled, and the engine, system and frame-pacing recorders are no longer created (nothing reads them without capture). No deployed config has capture off.
- Owner-grant expedite: a capture start resets its statistics only. It used to reset the per-second quota and the duplicate guard too, so the stop and restart at each 30-minute segment rotation could admit a duplicate or an extra forced send within that second.
- The heightmap budget's final export folds the frame still open when a capture stops after the late update (plugin shutdown).
- Module uninstall removes only the patches each module owns, one method at a time. Harmony's `UnpatchSelf` reads the body of every patched method in the process first, so one method that cannot load (seen in the offline contract harness) aborted it and left the module's later patches in place. In the game the end state is unchanged; plugin shutdown now logs a warning instead of throwing.
- Tests: the compression fallback runs for real in the contract harness (a valid frame is decoded after the failure limit and with the option turned off live, while sending stops).
- Caches: a biome or minimap cache file larger than `MaxEntryMiB` is refused before it is read, and minimap decompression stops at 256 MiB (the largest real entry is 36 MiB). The three minimap texture copies outside every stopwatch are timed (`minimap_cache_capture_ms`).
- CI: the video-memory test accepts zero budgets on an adapter without dedicated memory (the GitHub runner's display-only Basic Render Driver), which had kept CI red since 0.4.20.

### 0.4.20

- Fast portal arrival: never ends a teleport before the server has been sent the destination position (new blocker `server`, from `PositionJumpSync`'s send hook; without it, 2 s after the move). The settle check now counts from 0.3 s after that send and follows static objects only (pieces, trees, rocks, locations); creatures, item drops, projectiles, ragdolls, fish and birds are ignored.
  - Why: on 2026-09-30 a 3.1 s portal (client without the early position send) ended while the server was still streaming the old area. The empty destination looked settled, and a whole base appeared after the screen lifted.
  - New gauges: what reset settling, by category and distance band (`fast_arrival_settle_change_*`); time saved by ignoring moving objects; static objects still arriving in the 5 s after any distant arrival (`fast_arrival_late_static_*`, `fast_arrival_late_over_100`).
- Add opt-in map pin snap (`[Map] SnapPlayerPinsOnJumpEnabled`, client; `PlayerPinJumpMeters` 64). The game glides another player's map pin at 200 m/s, so a pin took 8 s to follow a 1.6 km portal and 20 s for 4 km, whatever the network did. A jump now places the pin at once; walking keeps the glide. Gauges `map_pin_snap_*`.
- Loot-visibility attribution v5: a candidate source stays only if the drop's own spawn stamp is within 1 s of the source's destruction. On 2026-09-30 every drop read over 1 s came from a mined vein and was paired with a single area; distance alone could not tell a mispaired drop from a late one that had rolled away. New gauges `loot_visibility_spawn_*`, `lag=` in witnesses. The look-back ring grows from 128 to 2,048 entries (26,000 eligible entries were overwritten in one session).
- Character save telemetry: every main-thread phase of a save is timed: player data (inventory, skills), map data, package build, file open, buffered writes, flush to disk, the character list reload, the backup copy. Also the unaccounted remainder, payload sizes, and the trigger (periodic, server, logout, sleep). It is the largest in-play stall left for one client (about 200 ms).
- Heightmap rebuild budget, observation only (decisions unchanged): critical rebuilds by distance band, cost, count per frame, the rule that made them critical, zone age and teleport context (`heightmap_budget_critical_*`). Ages that span a zone unload are reported apart (`heightmap_budget_age_across_reload*`): on 2026-09-30, deferrals of 33.8 s and 13.3 s were that, not waiting.
- Add system resource telemetry (`[Diagnostics] SystemResourcesEnabled`, `VideoMemoryQueryEnabled`, read-only):
  - video memory used and the budget Windows grants this process (DXGI);
  - system RAM and commit, machine CPU load and clock, mains or battery power;
  - process, system and session uptime;
  - hardware labels, and graphics proxy modules (ReShade) when one is loaded.
  - Why: one client's GPU frame time climbed from 17 to 42 ms over two hours at equal or lighter load, and frames over 50 ms rose from 3 to 120 a minute. Heat, video memory and power could not be told apart.
- Add spawn telemetry, read-only: creatures spawned per species, by day, night and event. Also refusals where the game decides them: species cap, no valid point, point inside a player-base area (ValheimPlus can widen it to 50 m), another one too close. The Mountain spawn table is logged once per world load.

### 0.4.19

- Add position jump sync (`[Teleport] SendPositionOnJumpEnabled` client, `RelayPlayerListOnJumpEnabled` server, both opt-in; `PositionJumpMeters` 64). After a portal or respawn the client sends its position at once instead of within 2 s, and the server relays the player list at once. The server chooses what to stream around that position, and the map draws players from that list. On 2026-09-26, other players' map icons lagged 0–4 s after fast portals. Same native messages and values. The delays are always measured (`position_jump_*`, `player_list_*`), docs/position-jump-sync.md.
- Teleport loading: add the 10 s after each distant arrival: slow frames, GPU or main thread, objects still pending creation at arrival and when the near ones drained (`teleport_after_*`, `Teleport after arrival:` log line). On 2026-09-26 the second player had about 5 more frames over 50 ms per portal in the 20 s after arrival than before fast arrival; 4 s capture intervals could not say why.
- Teleport loading: readiness reached in the very call that ends a teleport is now marked, instead of logging "area ready at never" (7 of 52 portals on 2026-09-26).
- Paint-only reload: split `terrain_reload_full` with `terrain_reload_full_first_load` (a zone coming into view) from real height edits.
- Add frame pacing telemetry (`[Diagnostics] FramePacingEnabled`, default on, read-only): stopwatch stamps in the engine loop around the frame-rate wait and each fixed step. Per-frame work against the 60/120/144 Hz budgets (`frame_busy_*`), the fixed-step catch-up cost and what a 0.1 s `maximumDeltaTime` would skip (`fixed_catchup_*`, `fixed_phase_ms_*`), and label `graphics_in_background` (false on the 30 fps isolated server: the limit is the requested preset). Two questions: a faster server tick (the dedicated server runs at 30 fps), and a lower catch-up cap (41 % of the second player's intervals replayed 3 or more fixed steps in one frame). Profiler markers could not answer them.
- Replication telemetry: time between two sync passes for the same peer (`replication_peer_interval_*`).
- Zone generation telemetry: attribute each new zone on the server to the nearest peer (`zone_generation_full_by_peer`, `zone_generation_full_worst_*`). Server spikes of 120–210 ms on 2026-09-26 came from live generation while exploring.

### 0.4.18

- Hourly asset unload deferral: a client now also offers the game's own unload check during a distant teleport (`[Memory] UnloadDuringTeleport`, default on). It runs once per teleport, after the move and once the destination area is ready, still behind the loading screen, and only if the last unload is over 20 min old, as vanilla does at sleep and respawn.
  - Why: on 2026-09-25 neither player slept or died for two hours, and the 120-min cap forced the unload in play on both clients (197 and 319 ms, their worst stalls of the evening), 1-2 min before their next portal.
  - Gauges `asset_unload_teleport_runs`, `asset_unload_teleport_ms_max`.
- Add a teleport loading timeline (`[Diagnostics] TeleportLoadingEnabled`, default on, observes only): per teleport, the time to the move, destination zone, active area, objects ready (`IsAreaReady`), floor and end, plus `teleport_ready_wait_ms`, the time spent after readiness, which on a portal is the fixed 8 s floor. Gauges `teleport_*`, docs/teleport-loading.md.
- Add opt-in fast portal arrival (`[Teleport] FastArrivalEnabled`, `MinimumSeconds` 3, `SettleSeconds` 0.75, `NearRadius` 80 m). A distant teleport ends before vanilla's fixed 8 s once everything near the player is loaded: zones, every received object of the 3×3 zones, ground, no queued terrain rebuild, grass patches, no dungeon being sliced, and the destination's object count unchanged for `SettleSeconds` (the server has finished sending). Vanilla's own `IsAreaReady` and `FindFloor` still run on the ending frame; a teleport is never held longer than vanilla. Gauges `fast_arrival_*`, including what held a teleport that kept the floor. Grass around the destination is built in one frame under the loading screen (the game's own full rebuild), instead of one patch per frame after arrival.
- Portal loading screen, opt-in (`[Teleport]`): `PrefetchTerrainEnabled` queues the destination's terrain (16 nearest zones) on the game's own builder thread during the 2 s fade; `ZoneBurstEnabled` (`ZoneBurstMilliseconds` 16) creates several destination zones per frame after the move instead of one per 0.1 s, with the same native call as the initial-join acceleration. On 2026-09-26 the zones around a destination took 2.8 s to appear and were what held an early arrival. Gauges `teleport_prefetch_*`, `teleport_zone_burst_*`.
- Add opt-in paint-only terrain reload (`[Terrain] PaintOnlyReloadEnabled`): when another player's terrain edit arrives and no height changed (hoe path, cultivator), refresh only the paint, as the editing player does, instead of rebuilding the collision and render meshes (5.3 ms mean on the second player's client on 2026-09-25). Height edits keep the full rebuild. Gauges `terrain_reload_*`, docs/terrain-paint-only-reload.md.
- Object creation budget: no longer applies while the game counts a loading screen, i.e. no local player yet or a teleport in progress (`[ObjectLoading] UnbudgetedInLoadingScreen`, default on). It capped creation at 4 ms per frame behind a screen that hides those frames, where vanilla allows 100 objects per frame. Gauge `object_budget_loading_screen_batches_total`.

### 0.4.17

- Idle pre-generation: fix its frame budget. On 2026-09-24 it generated 1 zone in 83 idle minutes.
  - Cause: the frame-start stamp sat before `TimeUpdate.WaitForLastPresentationAndUpdateTime`, where Unity sleeps to the target frame rate. An idle 30 Hz server (33 ms frames, ~3 % CPU) counted that sleep against the 8 ms budget, and 99.9 % of frames were refused (`idle_pregen_busy_frames`).
  - The stamp now follows that sleep (label `idle_pregen_frame_stamp`). A refused frame now shows as `waiting_frame_budget` or `waiting_busy_frame`; before, the previous status (`start_delay`) stayed displayed.
- Hourly asset unload deferral: on a dedicated server, the `MaxDeferMinutes` cap now counts from when players arrived, if that is later than the last unload.
  - Before, an unload done on the empty server before play started the clock: on 2026-09-24 that forced an unload mid-session, 97 min into play. Gauge `asset_unload_since_play_s`.
  - Clients are unchanged.
- Captures: when the directory allowance is reached, the plugin deletes its own oldest captures and keeps recording (`[Capture] PurgeOldestWhenFull`, default on). Before, recording stopped: on 2026-09-24 a client lost its last 13 minutes. Only files with the plugin's own naming are deleted.
- The server trims `captures/remote` to three quarters of `[Relay] MaxDirectoryMiB` at startup, oldest first (`PurgeOldestAtStart`, default on). Before, a full directory would stop every client's relay.

### 0.4.16

- Add opt-in deferral of the game's hourly unused-asset unload (`[Memory] DeferHourlyAssetUnloadEnabled`, `MaxDeferMinutes` 120). On 2026-09-23 the three loop gaps with no attributed method (306 ms server, 332 and 167 ms clients) were `Game.CollectResourcesCheckPeriodic`, scheduled every 3600 s, calling `Resources.UnloadUnusedAssets`. A client now leaves the unload to the native sleep, respawn and idle-pause checks; a dedicated server runs it once no peer is connected. Past the cap, vanilla runs it anyway. Gauges: `asset_unload_*`.
- Idle pre-generation prints console lines an operator can wait for: started (zones queued), finished or nothing to do, paused by a connecting player.
- Loot-visibility attribution v4. On the owner, t0 is now taken before the source's own drops exist: prefixes on `Destructible.Destroy`, `TreeBase.SpawnLog` and `MineRock.RPC_Hide`, since vanilla instantiates their drops before `ZNetScene.Destroy`. On a remote client, a drop ZDO that arrives up to 1 s before its source's removal can still match it (`loot_visibility_arrived_before_destroy`, `_lead_max`).
  - Why: on 2026-09-23 every slow case (> 1 s) paired a destructible with the next one's drops. The owner reported impossible 1.0-4.4 s own-instantiate times while both send legs stayed ≤ 71 ms.
  - Not a gameplay change.

### 0.4.15

- Minimap and biome caches: the key no longer contains the plugin version or this plugin's own entry in the mod list. It fingerprints every Harmony patch attached to the cached methods instead (`IlFingerprint`, any owner, all four patch kinds). Before this, every release invalidated both caches, and a verified hit needs three loads under one key, so neither cache had served a hit in three sessions while still paying 0.6-0.8 s of store time per join. Label `*_cache_patch_fingerprint_fallback` shows when a patch could not be fingerprinted and the plugin version was used for it. A change inside a helper that a patch calls is not in the key.
- Add an opt-in per-frame budget for queued terrain rebuilds on the client (`[Terrain] RebuildBudgetEnabled`, `RebuildBudgetMilliseconds` 4, `RebuildCriticalRadius` 80 m, `RebuildMaxDeferMilliseconds` 500). On 2026-09-23, `HeightmapLateBatch` caused 18 of the second player's 45 loops of 100 ms or more, with 10-128 rebuilds of 4-8 ms in one frame. Scope:
  - Only rebuilds vanilla queued with `Poke(2)` are skipped for a frame, and they stay queued. Those come from location terrain modifiers.
  - Order: nearest first, weighted by the camera view. Rebuilds inside the critical radius, overdue ones and those within grass distance always run, and the queue is paced to drain before the deadline.
  - Not touched: first builds, player digging and levelling, and `ForceGenerateAll`. Gauges: `heightmap_budget_*`.
- Add opt-in zone generation on a dedicated server while no peer is connected (`[ServerGeneration] IdlePregenerationEnabled`, `IdleRadiusZones` 2 rings beyond the synced simulation distance, `MaxZonesPerRun` 300). On 2026-09-23 the two server freezes (348 and 308 ms) were dungeon generation, which is atomic.
  - How: one `SpawnZone(Ghost)` per frame, the call `CreateGhostZones` makes, around zones where players were recently. The list persists per world.
  - Content does not depend on when a zone is generated: vegetation, location and dungeon seeds come from world seed and zone. Zones holding an unplaced unique location are skipped.
  - Stops as soon as a connection is accepted, and never runs during a save. It only helps if the server runs without players, e.g. started a few minutes before playing. Gauges: `idle_pregen_*`.
- Loot-visibility attribution v3:
  - t0 at the destroyed MineRock5 area's centre, not the rock root; sources this process owned are excluded for network arrivals.
  - Candidates are filtered by the source's drop table and a spawn radius per source kind. The copper-deposit fracture is a source kind; old drops re-entering view are excluded by spawn time.
  - A witness lists the slowest matches. New first-send legs on the owner client and on the server.
  - On 2026-09-23, 7 of our 11 slow windows (> 1 s) paired a network drop with a rock this client owned, whose drops can never arrive by network. So the v2 tail was at least partly attribution. A dedicated server now exports its send leg instead of zero observer gauges.
- Split `CharacterSaveToDisk` into phases, timed only inside `PlayerProfile.SavePlayerToDisk`: cloud checks, hash (`ZPackage.GenerateHash`, a full copy plus SHA-512), write, replace and auto-backup. Also `character_save_package_bytes`. With the map cache hitting, this 42-83 ms step is the largest remaining part of a save. Moving it off the main thread is not proposed: both characters here save to Steam Cloud through the game's save caches.

### 0.4.14

- Improve speculative map compression: write snapshot bits in byte-identical blocks, adopt a finished matching result at save time without waiting for the worker, and make publication/policy completion atomic. Add bulk-run and save-adoption counters; cache lookup timing includes adoption. Runtime savings and hit-rate changes remain unmeasured.
- Attribute direct `ZRpc.HandlePackage` elapsed time by method identifier, including generic callbacks, without moving the reader or inspecting payloads. Bounded export includes exceptions and skipped headers; direct and routed timings overlap.
- Correct loot-visibility attribution: freeze the source at network arrival, exclude missing arrivals, ambiguous sources and invalid chronology from timing distributions, and mark legacy/mixed reports. Local creation latency is now explicitly unavailable.
- Pair each mode's slowest zone spawn with its own heightmap, location, vegetation and dungeon samples. Missing phases stay unavailable and inclusive phases must not be summed. Generation behavior is unchanged.

### 0.4.13

- Add an opt-in capture relay (`[Relay]`, every key off by default): a client mirrors its capture records to the server it plays on as the writer puts them on disk (`SendCapturesEnabled`), optionally its BepInEx log too (`SendLogEnabled`, a bounded snapshot then live lines; the log names Steam IDs and characters, so it is a separate key for private servers), and a server with `AcceptEnabled` writes them under `captures/remote` with the client's own file name, bounded by `MaxDirectoryMiB`. The server offers first with `BP_RelayOffer`; a vanilla or older server drops that one RPC and nothing else happens. Records are framed with the 0.4.12 Deflate frame and split into 8 KB `ZPackage` chunks sent one per frame, only while the server link's send queue is empty and under `MaxBytesPerSecond` (64 KB/s default), so game traffic never waits behind the relay; a 30-minute client capture (33-42 MB raw, ratio 0.21) costs about 16 KB/s. Nothing is sent at exit or at save on purpose: the socket is gone in the frame the game disconnects, and a save is the worst moment to push 8 MB. The receiver validates the file name to a fixed shape, refuses a stream that does not start at sequence 0 or breaks (gap, corrupt frame, oversize, quota), and closes every file with a `relay_end` trailer naming the reason, which `summarize_capture.py` reports. Offline proof: a writer's output tees through the outbox into a sink byte for byte, footer included.
- Gauges: `relay_send_*` (messages, chunks, bytes raw/wire, pending, throttled, dropped, log snapshot) and `relay_sink_*` (peers, offers, streams, chunks, bytes received/written, rejected, quota used), `relay_failures`; labels `relay_status`, `relay_scope`.

### 0.4.12

- Replace BetterNetworking with two zero-configuration modules under `[Network]`, both roles, both yielding when BetterNetworking is still loaded. `NetworkFlow` (`AdaptiveFlowEnabled`) turns the fixed 10,240-byte `ZDOMan.SendZDOs` send gate into a per-peer window computed from Steam's own send-rate estimate, ping and pending bytes (bandwidth-delay product with a margin, clamped 16-256 KB, halved on a persistent backlog, grown back gradually), and pins the Steam send rate at 1 MB/s only on a LAN link (vanilla pins every link to 153,600 B/s; BetterNetworking pinned it to a menu value). `NetworkCompression` (`CompressionEnabled`) compresses Steam packets with a framed Deflate to peers running the same protocol version, negotiated with two RPCs and enabled only after the start message has left the send queue; a raw packet is always accepted, so a peer without the plugin stays vanilla. Measured need: on 2026-09-18 the second player's LAN Wi-Fi link backed up to 27 KB unacked at under 30 ms ping while loot ZDOs waited 1-4 s behind the fixed gate.
- Gauges: `net_flow_*` (window min/max, updates, back-offs, recoveries, LAN/WAN links, rate sets) and `net_compress_*` (packets and bytes raw/wire both ways, peers active/offered/incompatible, decode failures).

### 0.4.11

- Remove the smelter catch-up budget after its first real session showed it is a gameplay regression, not a deferral. Vanilla `Smelter.UpdateSmelter` replays a station's absence in one call and an iteration with no ore or fuel hits `continue`, so idle time is thrown away; the budget stopped after eight iterations and carried the rest in `s_accTime`, so an empty smelter came back with the whole absence banked (`smelter_accumulator_carried_max` 1,435 s, 1,789 truncations on 2026-09-18) and ore added afterwards was processed at 8x real time until the bank ran out, which ValheimPlus chest auto-feed turned into a whole chest smelted in minutes. `[Stations] SmelterCatchupBudgetEnabled` and `SmelterCatchupIterationsPerCall` are no longer read. The count-only smelter telemetry stays as `SmelterTelemetry`: `smelter_catchup_seconds_max` and `smelter_catchup_calls_over_60s` (the vanilla one-call replay size, read before the loop), `smelter_accumulator_carried_max` (must stay under 1 s in vanilla), spawn and ore-removal calls. Removed gauges: `smelter_loop_iterations_*`, `smelter_budget_*`.
- Add an opt-in server-side fix for the frozen player left standing in a portal after a teleport (`[Replication] SectorInvalidationFixEnabled`). `ZDO.InternalSetPosition` calls `SetSector` before it assigns `m_position`, so the per-peer `ZDOSectorInvalidated` decides with the old position and a single large jump never invalidates the ZDO on peers whose area it left; those peers are never resent the ZDO either, and keep the stale instance until the player crosses another sector at the destination. A postfix re-issues the native invalidation once the position is written. Gauges: `sector_fix_sector_changes`, `sector_fix_zone_jumps`, `sector_fix_invalidations_added`, `sector_fix_failures`; the module declines with `superseded_by_game` if a build assigns the position first.
- Loot-visibility probe: count MineRock5 area destructions in `loot_visibility_destroyed_rock` (it stayed 0 through 1,271 owner-side copper hits), and take t2 from the public `ZNetScene.AddInstance`, which `ZNetView.Awake` calls for locally instantiated and network-created objects alike, instead of `ZNetScene.CreateObject`, which only network ZDOs reach; owner-created drops now land in `arrival_missing` with their own perceived delay. Label `loot_visibility_scope` states the semantics.
- Host network gauges on a server: the dedicated-server build's `ZSteamSocket.GetConnectionQuality` calls the client Steam API, so `ZNet.GetNetStats` returned zeros for the whole 2026-09-18 session while two peers were connected. The plugin now reads each ready peer's connection through the game-server API (`SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus`) and exports the average and maximum ping, the minimum link qualities, summed byte rates, `host_net_pending_bytes_max` (the figure that gates `ZDOMan.SendZDOs`) and the measured peer count; a client also exports its own pending bytes. Status `server_per_peer`; native fallback if a contract is missing. The older per-peer `steam_*` gauges already used the game-server interface and stay; the two families differ only in aggregation.
- Local package copy: the conflict guard refused the fast path whenever any Harmony owner sat on `ZDO.Serialize`, and the plugin's own attribution telemetry has patched it since 0.4.4, so the optimization had been silently native (`native_due_to_method_patch`, 0 fast calls). Owners under the plugin id are now accepted; a foreign owner still disables the fast path and is named in `package_local_copy_conflict_owner`.
- Mined-drop placement: export `mining_hitpoint_seen_prefabs` (prefab name and the `m_hitEffectAreaCenter` value seen at Awake, bounded) so the next session says why 207 instances were seen and none overridden.
- Session analysis of 2026-09-18 (kept outside the repository): first real numbers for the loot probe (1,083 drops, p50 64-128 ms, 5.4 % above 1 s, network leg 254 ms mean vs 16 ms creation, all slow cases while the other player owned the object), the caches at their `stored` step, and the four player reports resolved.

### 0.4.10

- Follow Valheim 1.0.15 (2026-09-18). Its `TerrainComp.PaintCleared` spread no longer calls `neighbour.Save(paintOnly)` per painted zone-edge texel: it marks `m_modifiedPaint`, writes the mask in memory and pokes, and a later `Save` serializes the flags. That per-texel write was the whole cost `TerrainSaveCoalescing` existed to remove, so the module is superseded natively. The module is removed: this installation always runs the latest build, its own `Verify` already refused the 1.0.15 shape, and 300 lines of IL-shape code guarding a contract no supported build has would only rot. `[Terrain] CoalesceNeighbourSavesEnabled` is no longer read; a stale key in a config is ignored. The terrain attribution telemetry is unrelated and stays.
- Export the game's own network figures every interval through `ZNet.GetNetStats`: `host_net_ping_ms`, in/out bytes per second and Steam link quality (a client reports its server link, a server aggregates ready peers). The ping is the floor under every network leg the other probes report.
- Add an opt-in biome point cache. Valheim 1.0.14 disabled its own because the key was the world name plus one enum, so same-named worlds loaded each other's grid; `GenerateBiomePoints` is a pure function of seed, world-gen version and the generator binary and costs 4.3-5.0 s on every client join and server world load here. The plugin keys on seed, uid, seed name, versions, a token-independent fingerprint of every `WorldGenerator` method (plus Harmony owners) and the mod set, serializes through the game's own `Save`/`Load`, stores on a worker after native generation, and in `verified` mode serves an entry only after a byte-identical reproduction on a later load. `GenerateSectors`, which uses `UnityEngine.Random`, stays native. Research notes are kept outside the repository.
- Extend the loot-visibility probe from mined chunks to trees, logs, plain rocks and destructibles with a drop table: t0 comes from the owner's `ZNetScene.Destroy` (owned ZDO only) and the remote `OnZDODestroyed`; a `TreeLog` counts as loot beside `ItemDrop`; per-source destruction counts and the live `item_drop_instances` population are exported.
- Replace the initial-loading raw-IL hash pins with a token-independent fingerprint (`IlFingerprint`): the opcode stream and every operand, with member, type and string tokens replaced by the resolved member's full name. The four raw hashes broke on 1.0.14 and again on 1.0.15 while the two bodies stayed 179 and 97 bytes with unchanged logic; the fingerprint is identical on the 1.0.15 client and the 1.0.14 dedicated server, so one pin per method now covers both roles. Any opcode, constant, branch or referenced-member change still disables the module.
- Fix findings from a review of 0.4.8. Smelter catch-up budget: the gate-reset patches live in the telemetry set, so a telemetry install failure on a future build would have left the budget transpiler installed with a counter that never resets and every smelter frozen after eight loop tests; the budget now refuses to install without them. Dungeon spawn slicing: the in-bounds fallback read `m_originalPosition`, which only the spawning peer assigns and which a client never receives, so a player standing inside a custom-interior dungeon could be judged outside and see rooms placed under them across frames — the check now centres on the generator, doubles the vertical extent and adds a distance gate, all erring toward the native spawn; a throw during the final slice abandoned the *next* dungeon's slice because the finished entry was already dropped, now the slice in hand is abandoned; a destroyed generator's pending entry is removed by reference so it no longer pins the destroyed object. Map pre-compression: the policy's in-flight flags are now read and advanced under the same lock the worker writes them under, closing a visibility race that could throw and disable the module for the session, and the shared failure counter is atomic on both threads.
- Fix findings from a review of 0.4.7: the minimap cache's payload-size guard compared the entry to itself (now checked against the live textures' raw byte counts); an unreadable BepInEx plugin list now yields no cache key instead of a shared placeholder; the module header and doc say that a verified hit also skips the native cache write and delete. Mined-drop placement counts instances past its tracking cap and documents that its counter reports owner-side field overrides, not mining events; the GUI sound prefix no longer counts a suppression when there is no local player to hear it.
- Print the installed game version at the top of each game-contract harness run, and archive the previous build's assembly and decompilation before decompiling a new one (`docs/game-update-guide.md`), so the next contract break can be diffed instead of argued.
- The terrain game contract now covers only what the attribution telemetry hooks (`Heightmap.Regenerate`/`OnEnable`, the `PaintCleared` reachability of `spread`); the coalescing checks went with the module.

### 0.4.9

- Verify every native contract against Valheim 1.0.14 (2026-09-17). That update disabled biome-data caching, so `AltBiomeWorldData.VerifyBiomeData` now clears the cache and regenerates instead of calling `TryLoadCache`; the loading-details contract follows the new path and fails if the cache branch returns. `ZoneSystem.CreateLocalZones` and `PokeLocalZone` kept their exact body sizes, 179 and 97 bytes, while their pinned raw-IL hashes changed, which is metadata-token renumbering rather than a logic change: the four initial-loading fingerprints are re-pinned for both roles.
- Fix the game-contract harness aborting on the first failed module and silently skipping every later one. Each module now runs in its own scope, prints `FAIL <module>`, and a single exit lists all of them. The previously hidden failure this exposed was the initial-loading fingerprint above.
- Fix a terrain-paint loss window in save coalescing. `BeforeSave` advanced the native paint-hash gate before the deferred write existed; if the flush threw, the module disabled itself but left the gate claiming the edit was written, so the next native paint-only `Save` early-returned and that neighbour's paint was lost on reload. The gate is now rolled back to its pre-batch value when a flush entry fails. Never observed firing (0 fallbacks over 104 batches and 63 deferred saves in the recorded sessions), but the option is enabled in production on both roles.
- Add the loot-visibility probe: three postfixes time a mined chunk's disappearance to its ore being created locally, splitting the network leg from the local-creation leg on one process's clock, with an eight-bucket histogram of the perceived delay. Attribution is by position and time because the game records no link between a destroyed hit area and its drops; an absent arrival means this process created the drop itself. Client only, bounded, and free when nothing is being mined.

### 0.4.8

- Add opt-in smelter catch-up budget: a transpiler bounds the per-call `Smelter.UpdateSmelter` catch-up loop (default 8 simulated seconds per call); the native accumulator already carries the remainder, so totals, fuel use and timestamps are identical while a 20 to 34 ms return-to-base spike spreads over frames. Only the smelter family iterates; the other stations are O(1) and untouched.
- Add opt-in dungeon spawn slicing on the client: `DungeonGenerator.Spawn` places every room in one frame (24 ms mean, 45 ms max in forest crypts); the module replays the same per-room placement under a per-frame budget, holds the prefab release until the last slice, falls back to the native loop when the player is inside the dungeon bounds, and drops its queue entry on destroy. `bp_dungeon on | off | status`.
- Add opt-in speculative map pre-compression: a worker thread re-encodes and gzips a main-thread snapshot of the explored bit arrays through the game's own writer and primes the exact compression cache, so the synchronous character save hits it instead of paying 60 to 87 ms of native gzip. Byte identity is enforced end to end; a stale snapshot is an ordinary miss.
- Add count-only diagnostics ahead of three further optimizations: clutter patch generation (two timings, six counters), build-mode events (`BuildMenuOpen`, `PieceRemove`, `PieceCopy` timings, snap and clipping counters, a 10 ms placement bucket) and a seven-way `ZDO.SetOwner` caller split with per-cycle release/reclaim counters on the server. Log the unresolved cosmetic prefab names.
- Add six research documents (2026-09-17) with the verified game mechanisms behind each item.

### 0.4.7

- Add opt-in GUI group-sound deduplication: `InventoryGui.SetActiveGroup` creates the group effect even when the requested group is already active, stacking a second sound on the button click for the Craft/Upgrade tabs and recipe clicks; a prefix clears `playSound` in exactly that case and leaves the cycling path and every real group change alone.
- Add opt-in mined-drop placement at the hit point: clear the public `m_hitEffectAreaCenter` prefab field on the listed `MineRock5` prefabs so drops and hit effects use the pickaxe impact point instead of the destroyed chunk centre, which stops buried chunks dropping ore underground. Owner-side field override; `bp_mining on | off | status` toggles it in a running session for an A/B.
- Fix the minimap texture cache reporting `foreign_generate_patch` at every join: the plugin's own `WorldMapGenerate` timing probe patches `Minimap.GenerateWorldMap` under the main plugin id and is now an accepted owner, so the cache stores and serves again. Every other owner is still refused.
- Add 57 game-contract checks read from assembly metadata for the two new modules, and the documentation covering both. Both are off by default and self-report their status.

### 0.4.6

- Add 52 gameplay-loop timing probes: inventory and chest GUI, container interactions and changes, building placement, minimap explore and large map, ships and carts, tree/rock/destructible/wear/character damage and destruction, attacks, loot drops, smelter spawns, MonoUpdaters batches (crafting stations, SFX, instance renderer, smoke, floating, ships, transform and animation sync), item slow updates and auto-stacking, player/HUD/clutter/water updates.
- Add 44 bounded gameplay counters: chest opens granted or refused in use, concurrent open conflicts on the owner, container changes and GUI-open frames, inventory moves and sizes, smelter catch-up sizes, placement ghost frames, pieces placed and removed, tree/rock/destructible events, attacks and hits, drops, minimap explore updates and fog applies, ship and SFX instance counts, item auto-stacks.
- Omit idle timing summaries from interval records: an absent metric name means zero calls. Records shrink even though the probe set grew.
- Add the "Gameplay" report section, research on a two-player shared chest for ValheimPlus (design, race analysis, prior art, test plan) and its implementation prompt.

### 0.4.5

- Add opt-in Steam Cloud write buffer sizing: `SteamCloud.WriteFile` allocates a 100 MiB scratch buffer per write, three times per character save; the transpiler sizes it to the payload with identical bytes written and strict IL contract checks. Cloud write counters always export.
- Add opt-in minimap texture cache with shadow verification: the native world-map generation (about 6.3 s per remote join, because the native caches never load when `m_worldVersion` is 0 on a joined client) is compared byte-for-byte before an entry is ever trusted; verified entries skip generation. Keyed on seed, generator version, game/plugin version, generator IL hash and loaded mods.
- Add opt-in replication cadence: per-prefab minimum resend interval for cosmetic prefabs (fish, crows, seagulls) on the send path, lossless because every send carries full state; opt-in bird velocity publication so remote clients extrapolate instead of lagging. Replication telemetry (resend-interval and distance histograms, forced-send inserts, byte-budget truncations) always exports.
- Add opt-in server owner-grant expedite: an owner change applied from an incoming update is force-sent to the new owner, mirroring the container-open path, to shorten the second player's pickup tail. Grant counters and the ZDO count gauge always export.
- Add opt-in terrain neighbour-save coalescing: one `TerrainComp.Save` per neighbour per paint operation instead of one per painted edge vertex, byte-identical saved state. Regeneration attribution by reason and per-frame histograms always export.
- Split allow-listed routed RPCs (damage, terrain operation, ownership request, container open) by target prefab in a new `routed_rpc_target` attribution group; add damage-number pressure gauges.
- Add session analysis, research and roadmap documents for the 2026-09-15 session, plus a self-contained ValheimPlus map-sync PR prompt. Diagnostics remain on by default; every optimization is off by default and self-reports its status.

### 0.4.4

- Add base-simulation timing probes: structural wear batch and support recomputation, heightmap late batch, regenerate/modifier/collision/render rebuilds, terrain operations, crop/smelter/fireplace/cooking/beehive/sap/fermenter/windmill ticks, location, vegetation, zone and dungeon spawns. Add live population counts for pieces, heightmaps, legacy terrain modifiers and slow-update objects.
- Add bounded per-name attribution (top 24 plus a conserved "other" row) for object creation cost by prefab, serialized replication bytes by prefab and routed RPC dispatch by handler name. New `attributions` array in interval and end records; absent in older captures.
- Read Unity engine markers and counters through `ProfilerRecorder`: present/render-thread waits, GC.Collect pauses, frame times, GPU frame time (separate switch), draw/batch/triangle counts, plus fixed-step-per-frame accounting and GC mode. Availability is reported per metric at runtime.
- Add read-only host facts (process priority, affinity, timer resolution, power scheme, OS, shared performance counter for client/server alignment), Steam transport path (direct or relayed, relay POP), online backend, peer socket types, ownership request and ZDO manager counters.
- Extend the offline report with base simulation, attribution, engine, host/network and ownership sections. Diagnostics only: no gameplay, save, ownership or networking behavior changed; every new probe is switchable and self-reports unavailability.

### 0.4.3

- Add opt-in initial client loading acceleration: bounded extra native zone passes, unchanged readiness, native contract guards and conservative patch fallback.
- Add per-join cumulative work/cost counters and configuration state to captures and offline reports. Preserve observations across capture rotation; do not infer saved time from extra work counts.
- Expose one `InitialLoading.Enabled` switch; dedicated/listen servers and later respawns retain their original scheduling.

### 0.4.2

- Add passive biome cache/generation stage timings and native spawn-wait reason counters, preserving pre-capture observations with bounded storage.
- Report process-cumulative loading details without double-counting snapshots or treating readiness checks as seconds saved.
- Investigate loading-only zone bursts in the isolated QA harness; this experimental acceleration is not included in the gameplay DLL.

### 0.4.1

- Add bounded passive client loading timelines from accepted scene transition through readiness, character initialization and observed loading-screen release. Preserve pre-capture coverage and distinguish partial or interrupted episodes.
- Attribute inclusive join, world-generation, terrain-worker/wait, minimap-load and resource-check costs. Separate monotonic wall duration from the native accumulated-game-time spawn log.
- Keep repeated timeline counters cumulative per episode in offline reports; never add nested costs or infer missing milestones.
- Harden map serialization fallback for custom streams and local package-copy ownership guards for patched constructors/Clear.

### 0.4.0

- Add optional exact map compression reuse with complete native-input equality, bounded retained memory, native contract guards and late-patch fallback.
- Add optional local replication-package copy removal without changing wire format, ordering or ownership; expose avoided payload-allocation volume.
- Observe private committed and Unity allocator memory, optional sparse render timings and bounded local pickup/container outcomes with explicit ambiguity and partial-transfer semantics.
- Time native sector discovery, client/server replication sorting and aggregate character fixed updates; report neighboring bottleneck intervals offline.
- Add opt-in creation allowance after leading near preparation, with conservative nested/distant fallback and separate preparation/service telemetry. Preserve native readiness, order and count limits.
- Omit entirely idle action categories with explicit schema/coverage labels to reduce export allocations and log volume.
- Evaluate bounded immutable compression workers on desktop runtimes. Keep worker/save/AI architecture changes outside the installed plugin until Unity lifecycle and behavior invariants are validated.

### 0.3.3

- Observe allowlisted local graphics settings at capture start, after graphics application and during regular polls. Separate raw player preferences, active settings and synchronized simulation distances.
- Export bounded field-change history with observation times, source, latest snapshots and overflow counts. Retain pending changes at final export; document sampling and segment-gap limits.
- Add finer inclusive save/RPC timing categories and passive object-budget diagnostics, including bounded observed post-yield waits, expensive individual creations and censoring.
- Add aggregate AI cadence, pathfinding/spawn timing and Windows main-thread CPU accounting; no per-NPC scans or changes to AI behavior.
- Add optional bulk minimap bit serialization with exact native payload format, bounded workspace and strict fallback guards. Retain native compression, pins and persistence lifecycle; see the runtime validation for measured scope.
- Clarify that loot queue observations do not count pickups or chest transfers. Extend reports and offline regressions for configuration changes.

### 0.3.2

- Keep every measured game duration while sampling recorder self-cost once per 64 records; remove repeated self-timing work from the hot path.
- Reduce default loot scans to 250 ms cadence, 128 candidates and a soft 0.2 ms budget. Add one-second cooldown after a scan exceeds 0.5 ms; avoid clock/scene work for untracked creations.
- Back off polling after complete collector work exceeds 2 ms, up to ten-second exports with gradual recovery. Preserve continuous timing aggregation and expose coverage/cost counters.
- Request below-normal export thread priority. Add allocation, accounting and backoff regressions; no new gameplay optimization or runtime performance claim.

### 0.3.1

- Add optional continuous capture with session/segment identity and a non-destructive directory allowance.
- Add bounded passive loot-queue observations independent of loot-priority enablement; distinguish local observed waits, opportunities and censored coverage.
- Add structured slow-operation summaries with configurable method/loop/worker thresholds and interval context; no per-call log spam.
- Expose local `bp_mark` and extend reports for segmented sessions, slow windows and loot coverage.
- Prepare a two-second, 30-minute-segment real-session profile without QA. Validate offline tests and current client/server IL; new telemetry overhead and long-session behavior await gameplay measurement.

### 0.3.0

- Add opt-in quota expansion under the existing soft creation-time budget.
- Add opt-in nearby-loot priority within vanilla object-type tiers; retain vanilla order every fourth eligible pass.
- Bound prefab classification and scratch storage; expose option activity and fallbacks in captures.
- Following renewed test authorization, validate offline regressions and a sixteen-window headless comparison. Budget plus quota reduces mean observed loot availability from 268.57 to 158.73 ms in this workload; incremental priority results remain mixed. Document uncertainty and safety limits; no package release.

### 0.2.0

- Add an opt-in soft time budget shared by near/distant scene object creation.
- Preserve vanilla readiness, priority, count limits and invalid-prefab behavior; guarantee progress past failed prefabs.
- Add a main-thread runtime toggle and capture counters for controlled comparisons.
- Add budget boundary/progress regression tests and document latency tradeoffs.
- Validate one continuous eight-window client/server comparison: lower client creation-batch peaks, modest loop changes, variable loot latency; retain default-off status.

### 0.1.1

- Fix unavailable Windows Unity/Mono working-set readings with native PSAPI; omit invalid values.
- Add native Steam pending/unacknowledged bytes, rate, ping and queue-time estimates, separate from BetterNetworking-adjusted socket values.
- Support the dedicated Steam interface and known ServerSync buffering wrappers without altering their behavior.
- Add network-peer, RPC, save-update, sorted-object and distant-object timing probes.
- Add bounded scenario markers and identify loop gaps crossing phase boundaries or spanning GC collections.
- Extend reports with native counters, memory validity and marker interpretation.
- Add a two- or three-block comparison report with run-level ranges and exploratory paired 95% uncertainty intervals.

Diagnostics only. No spawning, saving, replication, compression or bandwidth behavior changed. BetterNetworking remains a separate optional mod.

### 0.1.0

- Initial bounded client/server diagnostics, background JSONL export and offline reporting.
