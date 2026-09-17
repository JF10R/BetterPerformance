# Play session 2026-09-16: executive report

Two-player session, 21:24 to 00:02 UTC (158 min), host client plus dedicated server on one machine with BetterPerformance 0.4.6, BetterNetworking 2.3.3, ValheimPlus 0.10.1.2 and the plant plugins. The second client has no plugin. Raw captures: 6 client segments (183 MB), 6 server segments (152 MB), 0 probe failures, 4 records dropped at file-size rotation (see below). Player reports: birds are fine; copper loot sometimes slow, missing, underground or too high; the Forge Craft/Upgrade tab plays its sound twice.

### Verdict

Client health matches the 2026-09-15 session: 165 fps average, not GPU-bound, 84 % main-thread CPU, seven character saves of 123 to 174 ms, no freeze above 250 ms outside the join. The server idles at 7.8 % CPU. The copper complaints are explained by vanilla `MineRock5` drop placement plus the vanilla 1 to 2 s terrain check, not by the plugin: every copper hit this session was handled by the host as owner, and the loot instrument saw no network-created loot wait above 89 ms. The tab sound cannot be measured by the capture and no plugin hook sits on that path. One plugin defect was found: the minimap texture cache disabled itself at join because the plugin's own timing probe on `Minimap.GenerateWorldMap` counts as a foreign patch, so the 6.8 s map regeneration ran again.

### Key numbers

| Signal | Client | Server |
| --- | ---: | ---: |
| Frames / average fps | 1,564,884 / 165 | 287,845 / 30 (paced) |
| Loop stalls > 50 ms | 208 (2 join, 7 saves, 6 terrain, rest 50 to 100 ms) | 108 |
| Longest stall outside join | 246 ms at 23:55:11 (terrain batch 84 ms) | 343 ms at 22:22:56 |
| Main-thread CPU (median) | 84 % | 7.8 % |
| GPU frame / CPU main frame (median) | 3.2 ms / 4.9 ms | headless |
| GC.Collect total / worst frame | 6.9 s / 20 ms | 5.7 s / 29 ms |
| Serialized replication sent | 425 MB | 393 MB |
| Steam transport | direct, ping 1 ms | direct, ping p50 1 ms, max 43 ms |
| Working set | 1.7 to 4.4 GB | 0.5 to 1.7 GB |

### The three player reports

1. **Birds.** `BirdVelocityEnabled=true`, 1 to 11 tracked bird instances, 0 bounded skips. Nothing to add; the report of correct behaviour stands.
2. **Copper loot: slow, missing, underground, too high.** The host handled all 367 `RPC_Damage` on `rock4_copper_frac` (the copper veins were host-owned during both mining windows, 22:48 to 22:55 and 23:18 to 23:19 UTC), so the drops were instantiated synchronously by the host, outside the object budget: `ObjectCreate` peaked at 1 to 2 ms in those windows and no loot-queue track started. The decompiled game explains all four symptoms without the plugin: `MineRock5.RPC_Damage` places each drop at the destroyed chunk's collider centre plus a 0.3 m random offset (a buried chunk drops underground, a top chunk drops high), and `ItemDrop.SlowUpdate` runs `TerrainCheck` on the owner only 1 to 2 s after creation and every 10 s after that, lifting an item to ground + 0.5 m only when it sits more than 0.5 m below ground. An item 0 to 0.5 m under the surface therefore stays invisible until picked up by the auto-pickup radius. Network-created loot near the host (224 tracks) waited at most 89 ms, mean 4 ms. The second player's view is not measured; her pickups of host-owned copper (46 `RPC_RequestOwn` on `CopperOre`) go client, server, host and back.
3. **Forge tab sound duplicated.** No capture metric covers audio (`audio_sources_playing` is skipped by design). The plugin hooks `InventoryGui.Show` and `InventoryGui.Update` only, both count/time-only prefix/finalizer pairs that cannot invoke a method twice; nothing hooks the tab handlers, `ButtonSfx` or `ZSFX` creation. ValheimPlus patches `InventoryGui.Show` (a flag), not the tabs. Vanilla `ButtonSfx` plays a click on `onClick` and a select sound on `OnSelect`, and the tab handler re-activates the recipe group, which reselects an element. Test to run: reproduce alone with `Capture.MethodTimings=false`, then with `Diagnostics.GameplayCountersEnabled=false`, then with BetterPerformance removed; if it persists, the sound is vanilla or ValheimPlus.

### Findings that deserve action

1. **Minimap texture cache never engages.** Labels: `minimap_cache_status=foreign_generate_patch`, `minimap_cache_result=none`. `MinimapTextureCache.Compatible()` requires every Harmony owner of `Minimap.GenerateWorldMap` to be its own id, and `TimingHooks` patches the same method under the main plugin id for the `WorldMapGenerate` probe (installed later in `Plugin.Awake`). The cache therefore disables itself on every join while `Capture.MethodTimings=true`. Cost this session: `WorldMapGenerate` 6.8 s inside a 7.1 s frame at 21:24:19. Fix: accept owners whose id starts with the plugin id. The biome cache also missed again (`TryLoadCache` false, `GenerateBiomePoints` 4.6 s), and `JoinPeerInfo` blocked 8.5 s; join stalls total about 17 s.
2. **Character save is still the recurring stall.** Seven saves at 21:43, 22:13, 22:42, 23:08, 23:38, 00:00 and 00:01 UTC, each inside the RPC dispatch that follows the server's world save: `CharacterSave` 123 to 174 ms, of which `MapSerialization` 60 to 87 ms and `CharacterSaveToDisk` 41 to 90 ms. Disk time is down from 130 to 202 ms on 09-15 (cloud buffer sized to payload); map serialization is now the larger half.
3. **Ownership churn grew.** Server `ZDO.SetOwner` 355,085 calls (37 per second, 4x the 09-15 session) and 4.0 M owner-unchanged grant checks; `replication_forced_send_entries` 3,194 with `ExpediteOwnerGrantsEnabled=true`. Not a stall, but the shared-base ownership traffic the 09-15 research targeted has not shrunk.
4. **Shared chests.** Host counted 7 concurrent-open conflicts and 11 in-use rejections over 216 open requests. The eight vanilla `Trying to add item to occupied slot -1, -1` errors (18:18 to 19:52 local) are the same two-player container race.
5. **Terraforming hitches remain.** 217 `RPC_ApplyOperation`, 4,822 heightmap regenerations at 4.8 ms mean, six `HeightmapLateBatch` frames of 50 to 85 ms.
6. **Fish still dominate server serialization**: Fish5 359 k, Fish1 126 k, Fish2 110 k serializations, ahead of Greydwarf 209 k and Player 160 k. The cosmetic resend interval covers only the host's own fish; most fish are server- or peer-owned.
7. **Dungeon and location spawns** run 24 ms mean, up to 45 to 48 ms, on the client (109 dungeon, 476 location spawns) while exploring crypts. Below the stall threshold but visible.

### Observed, not fixed

- Segments rotated on `MaxFileMiB=32` before the 1800 s duration and each rotation dropped one record and its `capture_end` (4 records total, loot censor counters lost at those boundaries). Either raise `MaxFileMiB` to 64 or lower `DurationSeconds` so the duration limit rotates first.
- The 24 `Failed to find rpc method 327122920` warnings are vanilla: `Container.Interact` invokes `discovered` while the class registers `RPC_Discovered`. Harmless.
- `Cosmetic resend interval resolved 13 of 14 configured prefabs`: one configured name does not resolve.

### Not measurable from this session

The second player's frame time, her local loot creation and pickup latency, and any audio event need the plugin on her computer or a manual A/B. Windows freeze correlation was not needed: no machine-wide stall occurred.

### Evidence

Captures: `Valheim/BepInEx/BetterPerformance/captures/20260916T21*-client-*.jsonl` and `Valheim dedicated server/BepInEx/BetterPerformance/captures/20260916T21*-dedicated_server-*.jsonl`; BepInEx `LogOutput.log` of both processes; game code read from a local ilspy decompilation (not committed). Routed RPC hashes were resolved with the game's stable string hash (`Step` 21,379 calls, `RPC_ResetCloth` 10,080, `SetTrigger` 6,317, `OnTargeted` 5,779 on the client).
