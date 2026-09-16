# Play session 2026-09-15: executive report

Two-player session, 23:23 to 00:39 UTC (76 min), host client plus dedicated server on one machine with BetterPerformance 0.4.4, BetterNetworking, ValheimPlus and the plant plugins. The second client had no plugin, so its experience is inferred from the host side only. Raw captures: 71 MB client, 58 MB server, 0 dropped records, 0 probe failures. Evidence for every number below is in the six JSONL segments and the generated report.

### Verdict

The host client is healthy: about 173 fps average, not GPU-bound, one 33-second join, five save stalls of 113 to 277 ms, one machine-wide 1.7-second freeze, and short 50 to 100 ms hitches while terraforming. The server is idle (6.7 % main-thread CPU). Nothing observed points at network transport: the link is direct Steam, ping 0 to 2 ms, no relay.

### Key numbers

| Signal | Client | Server |
| --- | ---: | ---: |
| Frames / average fps | 747,396 / 173 | 137,302 / 30 (paced) |
| Loop stalls > 50 ms | 90 (2 join, 5 saves, 1 host, rest 50 to 100 ms) | 19 |
| Longest stall outside join | 1,684 ms at 23:43:30 | 1,662 ms at 23:43:29 |
| Main-thread CPU (median) | 82 % | 6.7 % |
| GPU frame / CPU main frame (median) | 3.3 ms / 4.4 ms | headless |
| GC.Collect total / worst frame | 3.2 s / 22 ms | 2.1 s / 31 ms |
| Serialized replication sent | 208 MB (48 KB/s) | 123 MB |
| Steam transport | direct, ping 0 to 1 ms | direct, ping p90 2 ms, max 19 ms |
| Private commit | 4.6 to 5.3 GB | 1.7 GB flat |

### Strengths

- **Rendering headroom.** Present wait 0.02 ms per frame, GPU 3.3 ms, `WaitForTargetFPS` 1 ms per frame: the 185 fps cap is the limiter, not the GPU. Draw calls median 1,100, max 2,400.
- **Server.** Replication (`SendZdos` max 25 ms), sorting and discovery never stalled. World saves cost 72 to 130 ms synchronous plus 165 to 305 ms on the worker, five times.
- **Structural wear is not a problem.** `WearNTear` support recomputation ran 1.2 M times (17.5 s total, max 2.7 ms) for 700 to 860 pieces: about 0.4 % of wall time, no stall. ValheimPlus does not stop these calls, but they are cheap here.
- **Memory.** Client commit grew 0.7 GB, in steps that match exploration (instances 5,500 to 7,100), then held. No leak signature.

### Findings that deserve action or research

1. **Character save is the largest recurring stall.** Five saves: `CharacterSave` 113 to 266 ms, of which `CharacterSaveToDisk` 130 to 202 ms and `MapSerialization` 17 to 65 ms (map cache 2 hits, 3 misses while exploring). Research afterwards showed the cost is not disk I/O: the character is on Steam Cloud and each cloud write allocates a 100 MiB buffer, three times per save. See [character save research](character-save-research-2026-09-15.md).
2. **Join spends about 10 s regenerating caches the game already has.** `WorldMapGenerate` 6.3 s ran because `TryLoadMinimapTextureData` was never called; the biome cache load returned false, so `WorldInitialize` 2.6 s and `FindLakes` 1.8 s ran again. Join was 33 s transition to HUD. See the native biome cache research for the version mismatch; the minimap texture path is a new lead.
3. **Terraforming hitches.** 372 `RPC_ApplyOperation` terrain operations (up to 20 ms each) triggered 1,898 heightmap regenerations at about 9 ms each (regenerate + collision bake + render mesh), producing 50 to 100 ms frames during the 23:37 to 23:53 bursts. Research: coalesce regenerations per frame or defer the collision bake, with the readiness gates from the streaming research. Legacy terrain modifiers (about 100) cost only 0.2 ms per regeneration, so `optterrain` is a minor cleanup, not a fix.
4. **Machine-wide freeze at 23:43:29.** Both processes stopped 1.66 to 1.68 s at the same instant with no game method involved (`PlayerLoop` itself 1.66 s). Cause identified afterwards: Windows Update KB5129195 committed its component-store transaction at 19:43:29.717 local (Setup event 4, CBS log). The Virtual Disk Service stop five seconds later is a consequence, not the cause. A reboot is still pending. See [server/host research](server-host-research-2026-09-15.md) and the [roadmap](improvement-roadmap-2026-09-15.md).
5. **Replication is dominated by animals and fish.** Client to server: Player 21 %, Boar/Greydwarf/Skeleton/Deer/Neck/Bjorn about 45 %, three fish prefabs 18 % (430 k serializations, about 100 per second). Fish are cosmetic until fished. Research: whether fish and distant tamed animals can replicate less often without changing ownership or persistence.
6. **Ownership churn on the server.** 92,965 `ZDO.SetOwner` calls (20 per second) with two players sharing a base, and 26,347 dead-ZDO tombstones retained. The host client handled 193 `RPC_RequestOwn` and 138 chest-open requests, mostly the second player's, so her loot and chest latency runs client, server, host client and back. Her pickups on his objects inherit every host hitch above.
7. **Damage RPCs cost 1.2 ms each.** `RPC_Damage` 1,668 calls, 1.95 s, the top routed RPC on the client (28 %). Footstep `Step` RPCs are 17 % by volume (8,342 calls). Worth one profile of `WearNTear.RPC_Damage` and `Character.RPC_Damage` to see whether effects or support recomputation dominate.
8. **Peer disconnect stalls the server 105 ms** (23:28:35, 00:19:09, 00:36:46) and ValheimPlus map sync 54 to 68 ms at join. Rare, but each one is a frame the other player waits on.
9. **Chest-open outlier.** One local container open waited 1,979 ms (137 others confirmed fast, mean 15 ms); three ownership-gated pickups waited up to 530 ms. Single events, not a trend.

### Not measurable from this session

The second player's frame time, Wi-Fi jitter and local creation latency need the plugin on her computer; the QPC alignment between client and server is still unvalidated by a known-simultaneous event. `Physics.*`, `Animator` and per-frame allocation markers are unavailable in this Unity build.
