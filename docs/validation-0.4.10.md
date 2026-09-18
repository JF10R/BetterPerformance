# Validation: 0.4.10

2026-09-18 (local). A second game update in two days (Valheim **1.0.15**, client and dedicated
server), a review of 0.4.7-0.4.10 with its fixes, the removal of a superseded module, one new
optimization and new measures. Plugin DLL SHA-256
`BA34F38D814D853D002129F74E4B4C198E02815F2C4C0DF96AE10A6E57A04439`. Review:
[review-2026-09-16-18.md](review-2026-09-16-18.md); biome cache:
[biome-point-cache.md](biome-point-cache.md) and its
[research](biome-generation-research-2026-09-18.md).

### What 1.0.15 changed for this plugin

`TerrainComp.PaintCleared`'s `spread` no longer saves the neighbour per painted texel, the
whole cost `TerrainSaveCoalescing` removed; the module was deleted (this installation always
runs the latest build). Nothing else drifted: the initial-loading fingerprints, now
token-independent and spelled by our own code rather than `MemberInfo.ToString()`, are identical
on the 1.0.15 client, the 1.0.14 server build and the 1.0.15 server build.

### Offline gates

| Gate | Result |
| --- | ---: |
| Release build, `-warnaserror`, client and server references | exit 0, 0 warnings |
| Core/offline suite | 47/47 (biome cache store added) |
| Python report suite | 70 tests OK |
| Game-contract harness, client 1.0.15 | exit 0 (initial loading 319, biome cache 23 incl. the module's own lookups executed, loot visibility 29, terrain 47) |
| Game-contract harness, dedicated server 1.0.15 | exit 0, same counts |

### Isolated runtime session

Headless client plus dedicated server on the disposable `bp_test_` world, temporary character,
63 real save files verified unchanged. Run root `.qa/runs/20260918T220501Z-4bd933`, variant
`fast_join_production`, ValheimPlus upstream `main` (0.10.1.2 + 3) on both roles.

| Observation | Client | Server |
| --- | ---: | ---: |
| Probe failures / dropped records | 0 / 0 | 0 / 0 |
| `initial_loading_status` | installed; 100 native calls, 49 extra, 48 successes, 0 failures | `disabled_at_startup` (by design) |
| `biome_cache` | installed, verified mode, `stored` (1 entry, key 15 ms, serialize+store 602 ms on the worker) | same, `stored`, 638 ms; identical key `ee125464…` on both roles |
| `loading_biome_GenerateBiomePoints_last` / `GenerateSectors_last` | 4,629 / 554 ms | 4,627 / 570 ms |
| `loot_visibility_status` | installed | `dedicated-server` |
| `host_net_status` | `client_server_link`, 0 ms (loopback), 0.6 KB/s in, 9.1 KB/s out | `server_peer_aggregate` |
| smelter / dungeon / mining / terrain attribution / map pre-compression | installed | installed |
| Any `*_status` reporting failure or unsupported layout | none | none |

Two earlier attempts of this run failed for reasons outside the plugin and are recorded because
each cost an hour: the first because the client carried a stale dev build of ValheimPlus branch
`pr163-v2` while the server ran a fork build, so ValheimPlus's own handshake rejected the client
(`ErrorVersion`); the second because the host's commit charge (79 of 98 GB) made Unity fail to
create a thread at startup (`0x5af`). The first attempt also exposed that the fingerprint hashed
`MemberInfo.ToString()`, which Mono formats differently from the harness CLR — fixed before this run.

### Regression check

Module statuses match the 2026-09-17 1.0.14 runs except where intended: the terrain coalescing
labels are gone with the module, `biome_cache_*`, `host_net_*` and the loot-visibility source
counters are new.

### Unproven

The biome cache has never served an entry: the harness generates a fresh world per run, so
promotion and the first hit need three loads of the same real world (`biome_cache_result`
`stored` → `promoted` → `verified_hit`). The extended loot-visibility probe and the net-stats
gauges have no non-zero measurement yet. Dungeon slicing's corrected in-bounds fallback,
smelter budget guard, map pre-compression lock and minimap payload check are verified by
contract and offline tests, not by a rendered session. ValheimPlus upstream `main` on 1.0.15 is
validated only as "builds clean, loads, and its handshake accepts the pair".
