# Validation: 0.4.5

2026-09-15 (local). Six opt-in modules and their telemetry, built against Valheim 1.0.12 / Unity 6000.0.75f1. Every optimization is off by default in the repository configuration and self-reports its status; the user's deployed configuration enables them as listed at the end.

### Offline gates

| Gate | Result |
| --- | ---: |
| Core/offline suite | 44/44 |
| Python report suite | 57 tests OK |
| Game-contract harness, client installation | exit 0 (cloud write 132, minimap cache 28, replication 62, owner-grant 45, terrain 82, attribution 81 checks) |
| Game-contract harness, dedicated server | exit 0 |

The standalone harness reads IL through Mono.Cecil where the CLR cannot type-load Unity or Steam types, so Harmony installation itself is reported STATIC ONLY there; contracts, malformed-layout rejection and pure logic are what it proves.

### Isolated runtime session

One headless client plus dedicated server on the disposable `bp_test_` world with the temporary character, all six switches on, minimap cache in shadow mode. Run root `.qa/runs/20260916T022236Z-6e2c4f`; 180 real save files and the preferences registry verified unchanged.

| Module | Observed | Exercised |
| --- | --- | --- |
| Cloud write buffer | installed on the client; `type_unavailable` on the server (no Steam cloud backend) | No: the isolated character is local, so no cloud write happened |
| Minimap texture cache | installed, shadow, result `none` | No: the headless client never generates the world map |
| Replication cadence | 27,580 entries considered on the client, 17,590 deferred (fish, seagull), first sends never deferred, prioritized entries untouched | Yes |
| Bird velocity | 2,438 publications for one tracked seagull, no exception | Yes (owner side; remote extrapolation needs a second client) |
| Owner-grant expedite (server) | 32 grants observed, all skipped as sender or offline peer, 0 forced | Counters only; a grant to a second peer needs two clients |
| Terrain coalescing | installed, native pass-through poke | No: the workload performs no terrain operation |
| Attribution target split | installed; no allow-listed RPC with a target occurred | No |
| Probe failures / dropped records | 0 / 0 on both roles | |

Server-side replication telemetry also recorded 27 byte-budget-truncated sends holding 107,395 entries during the initial world transfer, which is the native join behavior, not a defect.

### What the next real session must confirm

- Character save: `CharacterSaveToDisk` should fall from 130 to 202 ms toward the map-serialization cost; `cloud_write_sized_allocations` counts the patched writes.
- Minimap cache: first join stores (`miss`), second join `shadow_match`, third join `verified_hit` with no `WorldMapGenerate` sample. Any `shadow_mismatch` permanently disables that key and must be reported.
- Replication: `prefab_send_bytes` for fish and birds should drop by about the deferral ratio; watch fishing and bird landing for visible regressions.
- Ownership: `ownership_grants_expedited` > 0 on the server when the second player picks up host-owned items.
- Terrain: `terrain_saves_deferred` > 0 during terraforming and no visible paint difference.

### Deployed configuration

Client: `CharacterSave.CloudWriteBufferSizedToPayload=true`, `MinimapCache.Enabled=true` with `Mode=verified`, `Replication.CosmeticResendIntervalEnabled=true`, `Replication.BirdVelocityEnabled=true`, `Terrain.CoalesceNeighbourSavesEnabled=true`. Server: the replication and terrain switches plus `Ownership.ExpediteOwnerGrantsEnabled=true`; minimap cache off. All other values preserved.
