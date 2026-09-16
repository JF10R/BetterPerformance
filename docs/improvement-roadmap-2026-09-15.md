# Improvement roadmap after the 2026-09-15 session

Consolidates six research documents written against the decompiled Valheim 1.0.12 build, ValheimPlus 0.10.1.2, BetterNetworking 2.3.3 and the session captures. Every row links to the document holding the evidence and the falsifying experiment. Nothing here is implemented yet; all optimizations stay opt-in and default off, as before.

### Verdict

Three findings change what to build first. The character-save stall is a 100 MiB buffer allocated three times per Steam Cloud write, not disk I/O. Both join-time caches are write-only on a client that joined a remote server because `m_worldVersion` stays 0. The second player's pickup path has no reply RPC and no forced ZDO send, unlike the container path. The 1.7-second freeze was Windows Update KB5129195 committing, not the game.

### Ranked candidates

| # | Candidate | Expected effect | Risk | Effort | Evidence |
| --- | --- | --- | ---: | ---: | --- |
| 1 | Size the Steam Cloud chunk buffer to the payload (`SteamCloud.WriteFile`, 100 MiB → payload length, identical bytes written) | Removes most of the 130 to 202 ms per character save and 3 to 4 GC collections per save | Low | ~10 lines + parity test | [character save](character-save-research-2026-09-15.md) |
| 2 | Plugin-owned exact cache of the minimap world texture (seed + generator version + code hash key, shadow-compared before use) | Up to 6.3 s off every join | Medium (a wrong map is visible) | Medium | [join cache](join-cache-research-2026-09-15.md) |
| 3 | Per-prefab minimum resend interval for cosmetic prefabs on the send path (fish, then birds after velocity fix) | About 15 to 20 % of replicated bytes; lossless because every send is full state | Low | Small | [replication](replication-research-2026-09-15.md) |
| 4 | Server-side expedite of owner grants (`ForceSendZDO` to the new owner on owner change) | Targets the 530 ms pickup tail for the second player; no data or save change | Low | Small | [ownership](ownership-latency-research-2026-09-15.md) |
| 5 | Coalesce `TerrainComp.Save` neighbour saves in `PaintCleared` (one save per neighbour per operation) | Removes most of the 50 to 100 ms terraforming frames; saved bytes identical | Low | Small | [terrain](terrain-regeneration-research-2026-09-15.md) |
| 6 | Publish bird velocity so non-owners extrapolate; debounce LOD blanking on ownership hand-off | Fixes the jittery birds; unlocks 3 for birds | Medium (remote motion changes) | Small | [replication](replication-research-2026-09-15.md) |
| 7 | Net-change `ReleaseNearbyZDOS` (apply only net owner transitions per 2 s cycle) | Cuts the 20 SetOwner/s churn and the resends it forces | Low-medium (owner set must match vanilla exactly) | Medium | [ownership](ownership-latency-research-2026-09-15.md) |
| 8 | Distance-gated resend interval for AI creatures beyond 32 m, not in combat | Up to ~44 % of replicated bytes in scope | Medium (never gate a creature targeting a player) | Medium | [replication](replication-research-2026-09-15.md) |
| 9 | Pregenerate cache (rivers, lakes, streams in native order) | About 2.6 s off join | Medium-high (feeds terrain everywhere) | Medium | [join cache](join-cache-research-2026-09-15.md) |
| 10 | Skip `RebuildRenderMesh` for ghost zone spawns; defer render mesh one frame on terraform | ~2.3 ms per regeneration on exploration and terraform paths | Low-medium | Small | [terrain](terrain-regeneration-research-2026-09-15.md) |
| 11 | Pool `DamageText` world texts and expire all due entries per frame | Client visuals only; combat unchanged | Low | Small | [server/host F1](server-host-research-2026-09-15.md) |

Sequence: 1 and 4 first (smallest, both target the second player's felt latency and the host's largest recurring stall), then 3 with 6, then 2, then 5, then 7, 8, 9, 10, 11. Each ships behind its own switch with the existing shadow-parity and disposable-world protocol.

### Zero-code actions for the next session

- Move the test character to local storage to confirm finding 1 before patching; do not toggle Steam Cloud on the real account.
- Reboot: KB5129195 is still pending a restart, and pending servicing keeps retrying. Set Active Hours over the gaming window or pause updates before a long session.
- Install the plugin on the second computer: her frame time, Wi-Fi jitter and pickup latency are otherwise unmeasurable.

### ValheimPlus map sync

Exploration sync is active in 0.10.1.2 and costs 53 to 68 ms on the server main thread per join: a per-bit scan of 4.2 million pixels, re-broadcast to every peer, applied client-side pixel by pixel. Pin sharing is dead code (the shareable-type list is never populated), which upstream PR #163 restores; it does not touch the exploration cost. Suggested separate PRs, in order: correctness (off-by-one at run ends, null guard on empty worlds), then word-level scan plus dirty flag plus send-to-joiner-only, then bulk `SetPixels32` on the client, then a binary disk format with main-thread snapshotting. See [server/host F3](server-host-research-2026-09-15.md).

### Rejected after research

- Async worker for the character save as the first move: the allocation fix removes the need; the worker carries the whole coherence-invariant list for little extra.
- Dead-ZDO trimming: no latency path, and removing tombstones re-enables item duplication.
- Server-owned shared containers, proactive ownership hand-off, wider release radius: behaviour changes with negative expected value.
- Async collision bake on the terraform path: worst gain-to-risk ratio; Valheim Community Patch excluded it on purpose.
- Legacy terrain modifier migration (`optterrain`): 0.2 ms per regeneration, about 4 %.
- Spreading the 105 ms disconnect sweep: 0.007 % of wall time on an idle server, against a window where orphan ZDOs stay owned by a gone peer.
- Forcing `m_worldVersion` on a joined client or rewriting the native cache files.

### Telemetry to add before or with the work

Per-target split of `RPC_Damage` by prefab; regenerations per frame and reason (terrain op vs zone load); per-prefab resend interval and distance-at-send histograms; forced-send queue inserts and byte-budget truncations on the server; a per-RPC round-trip via message IDs for the second player's requests.
