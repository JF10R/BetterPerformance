# Minimap texture cache

Optional, default off. Plugin-owned cache of the three textures `Minimap.GenerateWorldMap`
produces. It exists because the native minimap cache is write-only on a remote join:
`ZNet.World.m_worldVersion` is `0` on a joined client, so `TryLoadMinimapTextureData` always
rejects its own files ([join-cache research](join-cache-research-2026-09-15.md)).

## Mechanism

A Harmony prefix/postfix pair on `Minimap.GenerateWorldMap`. The postfix reads back
`GetRawTextureData()` from `m_mapTexture`, `m_forestMaskTexture` and `m_heightTexture` and
stores it. The prefix may skip native generation and restore those raw bytes with
`LoadRawTextureData` + `Apply`. Raw storage bytes are cached, not the pre-quantisation
arrays, so the round trip is exact for the shipped formats (RGB24, a 4-bit mask format,
RHalf). Entries live in `BepInEx/BetterPerformance/minimap-cache/<key>.bin` (gzip, SHA-256
over the payload). The native `cacheMinimap*` files are never read, written or deleted.

## Key

SHA-256 over: world seed, `m_worldGenVersion`, `Version.GetVersionString()`, plugin version,
`m_textureSize`, `m_pixelSize`, the three textures' size/format/mip layout, a hash of the IL
and Harmony patch owners of every game method transitively reachable from `GenerateWorldMap`
and the `WorldGenerator` constructor/`Pregenerate`/`VersionSetup`/`Initialize`, and the sorted
set of loaded BepInEx plugin GUIDs and versions. World name is never part of the key.

## Modes

`[MinimapCache] Enabled` (false), `Mode` (`shadow` | `verified`), `MaxEntryMiB` (64),
`MaxDirectoryMiB` (256).

- **shadow**: always generate natively, compare against the stored entry byte for byte, then
  store. An entry is marked verified only when a previous entry compared identical.
- **verified**: additionally skip native generation when a verified entry matches the key and
  the live texture layout. Otherwise it behaves as shadow.

## Invariants

- Nothing is ever applied that the installed game did not itself produce twice identically.
- A key that once mismatched can never be promoted; the mismatch count persists in the entry.
- Installation fails closed if `GenerateWorldMap` writes any state beyond the three textures,
  allocates, takes a field address, calls an unexpected type, or is already patched. Any
  runtime error disables the module and leaves native generation in place.
- A failed or partial restore returns to native generation, which rewrites all three textures.
- `m_explored`, `m_exploredOthers`, pins and fog are never touched.
- Behavioural delta on a verified hit: `DeleteMapTextureData` and `SaveMapTextureDataToDisk`
  do not run, so the native cache files are left as they were. They are unreadable on a
  joined client regardless.

## Telemetry

Labels `minimap_cache_status`, `minimap_cache_mode`, `minimap_cache_result`
(`miss` | `miss_unverified` | `shadow_match` | `shadow_mismatch` | `verified_hit` |
`load_failed` | `store_failed` | `key_failed`). Gauges `minimap_cache_native_ms`,
`_load_ms`, `_compare_ms`, `_store_ms`, `_key_ms`, `_entry_bytes` (last serialized entry) and
attempt/hit/mismatch/failure counters. One BepInEx line per generation.

## Validating on the disposable world

Graphical client only: `Minimap.Update` returns early without a graphics device, so the
headless harness cannot exercise this path.

1. First join with `Enabled=true`, `Mode=shadow`: expect `result=miss`, an entry on disk.
2. Second join: expect `shadow_match` and the entry promoted to verified.
3. Third join with `Mode=verified`: expect `verified_hit` and `native_ms` no longer growing.
4. Negative controls: change the seed, add a world-generation mod, truncate the entry. Each
   must miss and fall back to native generation.

## Limits

Restore cost, `LoadRawTextureData` fidelity and the real saving are unmeasured until a Unity
run. The offline verifier cannot read every reachable body, so key computation is only proven
in-game; it fails closed when it cannot read one.
