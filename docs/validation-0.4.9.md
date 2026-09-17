# Validation: 0.4.9

2026-09-17 (local). A game-update response plus one new diagnostic. Built and verified against
Valheim **1.0.14** (`assembly_valheim.dll` SHA-256 `F64998168A0DD37EC774816808F914ED68376BE1B9670CD05A6C2F27C8017FB6`,
client and dedicated server both updated that morning); 0.4.8 was verified against 1.0.12. Plugin DLL
SHA-256 `18BE44F9A7EDB2B0AAFF06B5AF295C8C7D2442CD976F72C8384CFC0E800B496D`. Procedure followed:
[game-update-guide.md](game-update-guide.md) steps 1-4. Probe design:
[loot-visibility-latency-2026-09-17.md](loot-visibility-latency-2026-09-17.md).

### Contracts that drifted with 1.0.14

| Contract | Change | Response |
| --- | --- | --- |
| `AltBiomeWorldData.VerifyBiomeData` | The patch note's "Disabled biomedata caching" removed the `TryLoadCache` branch; it now calls `RemoveCache` then generates unconditionally | Contract follows the new path and also fails if the cache branch returns. `TryLoadCache`/`SaveCache` still exist and stay hooked, so that stage reports zero calls |
| `ZoneSystem.CreateLocalZones`, `PokeLocalZone` | Both pinned raw-IL hashes changed | Re-pinned for both roles. Body sizes stayed 179 and 97 bytes and the decompiled bodies still carry the repeat-call behaviour the module needs, so this is metadata-token renumbering. Stated as an equivalence argument, not a byte diff: the previous build's IL no longer exists on this machine |

### Harness defect this exposed

The game-contract harness threw on the first failing module and skipped the 13 after it, so the
initial-loading break stayed invisible behind the loading-details one for an unknown number of runs.
Each module now runs in its own scope and one exit lists every failure. The second break appeared on
the first run after the fix.

### Terrain paint-loss window, found while auditing 1.0.14 overlap

The patch note's "terrain modifications becoming undone" prompted a read of
`TerrainSaveCoalescing` against the current native code. The native fix and the module sit on
different layers — the game changed in-memory reads (`getMask`, `TerrainComp.cs:705-711`) and
added `m_lastDataRevision` (`:162`) so an owner stops reloading over its own edits, while the
module defers only the ZDO write — and the module reduces revision bumps N to 1 per neighbour,
which pushes the same way as their multiplayer fix.

The defect found was ours: `BeforeSave` advanced the paint-hash gate before the deferred write
existed, and a throwing flush left the gate claiming a write that never happened, so the next
native paint-only `Save` early-returned and that neighbour's paint was lost on reload. Fixed by
rolling the gate back to its pre-batch value on a failed flush entry. It has never fired:
`terrain_coalesce_fallbacks` is 0 across every recorded capture, over 104 batches, 63 deferred
and 9 flushed saves on the client (the server recorded no batches — terraforming was host-owned).
The option is enabled on both roles here, so the window was live, not theoretical.

### Offline gates

| Gate | Result |
| --- | ---: |
| Release build, `-warnaserror`, client and server references | exit 0, 0 warnings |
| Core/offline suite | 46/46 |
| Python report suite | 70 tests OK |
| Game-contract harness, client | exit 0 (initial loading 319, loading details 42, loot visibility 16) |
| Game-contract harness, dedicated server | exit 0, same counts |

### Isolated runtime sessions

Headless client plus dedicated server, disposable `bp_test_e2854dba07` world and temporary
character; 63 real save files verified unchanged on each run. Three runs: `fast_join_baseline`
(`.qa/runs/20260917T130553Z-106988`), `fast_join_production` (`…T130931Z-b8a6e1`), and the final
one on the shipped binary (`…T133629Z-9219e8`, after the terrain rollback fix; the earlier `…T132140Z-89f090` run covered the same variant before it).

| Observation | Client | Server |
| --- | ---: | ---: |
| Probe failures / dropped records | 0 / 0 | 0 / 0 |
| `initial_loading_status` | installed; 107 native calls, 48 extra, 48 successes, 0 failures | `disabled_at_startup` (by design) |
| `loot_visibility_status` | installed, 0 probe failures | `dedicated-server` |
| Any `*_status` reporting a failure | none | none |

The initial-loading row is what proves the re-pinned fingerprints at runtime, not only in the test:
the module's own guard accepted the native bodies and the burst ran. The `fast_join_baseline`
variant cannot show this because it leaves the module disabled.

### Regression check against the previous build

Module statuses from a pre-update run (`.qa/runs/20260917T014402Z-3cc8a5`, 01:44, before the 08:46
client update) differ from the post-update runs only where intended: initial loading by variant, and
the new probe. `wear_updates_per_frame_status: unavailable_instance_field` and
`package_local_copy_status: native_due_to_method_patch` appear identically on both sides of the
update and are therefore pre-existing, not 1.0.14 regressions.

### Unproven

The loot-visibility probe has produced no non-zero measurement: the headless workload mines nothing,
so every duration counter is zero and only installation, export and the dedicated-server path are
verified. Its first real numbers need a play session. Rendering, Steam cloud writes, terrain
operations and second-player ownership keep their existing "unproven at runtime" notes. Other
installed mods were present in the isolated runs without incident, which is not a validation of
their own contracts against 1.0.14.
