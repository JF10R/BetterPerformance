# Biome point cache

Opt-in, default off: `[BiomeCache] Enabled`, `Mode` (`shadow` | `verified`), `MaxEntryMiB` (32),
`MaxDirectoryMiB` (128). Requires a restart. Applies to a client join and to a dedicated
server's world load alike. Research: biome-generation-research-2026-09-18.md.

## Why

Valheim 1.0.14 disabled its biome-data cache ("the cache could end up in an invalid state,
making it impossible to join remote servers"). Its key was the world *name* in a local
directory plus one `Version.World` enum — no seed, no uid — and `RPC_PeerInfo` never set that
enum on a client, so two worlds sharing a name loaded each other's grid and `GetBiomeSector`
answered confidently with the wrong sectors. `VerifyBiomeData` now always runs
`GenerateBiomePoints` + `GenerateSectors`: measured here at 4,295 + 610 ms on a client join and
4,986 + 693 ms on a server world load.

`GenerateBiomePoints` is a pure function of the world seed, `m_worldGenVersion` and the
generator code: 2048 × 2048 points of `WorldGenerator.GetBiome`/`GetBiomeHeight`, no Unity
object, and the game already calls those from `HeightmapBuilder`'s worker thread.
`GenerateSectors` is not pure (`GenerateAltBiomes` uses `UnityEngine.Random`) and stays native.

## Mechanism

A prefix/postfix pair on `AltBiomeWorldData.GenerateBiomePoints(World)`.

- Key: SHA-256 over `Version.GetVersionString()`, `m_seed`, `m_seedName`, `m_uid`,
  `m_worldGenVersion`, the grid size, the `IlFingerprint` of every `WorldGenerator` method and
  constructor plus `GenerateBiomePoints`/`MapSpaceToWorldSpace`, every Harmony patch attached
  to each (any owner; declaring type, name, priority and an `IlFingerprint` of the patch
  method), and the sorted BepInEx plugin set excluding this plugin's own GUID. This plugin's
  version is never part of the key, so a BetterPerformance release no longer invalidates
  every entry; a patch method Harmony cannot describe falls back to this plugin's version for
  that one patch (`biome_cache_patch_fingerprint_fallback`). Any other unreadable component
  yields no key and native generation runs (`biome_cache_result=key_failed`).
- Postfix, after native generation: serialize `world.m_biomeData` through the game's own
  `Save(BinaryWriter)` on the main thread (~21 MiB), then on a worker compare with the stored
  entry: absent → stored unverified; identical → promoted to verified; different → stored
  unverified with the mismatch counted. Entries live in
  `BepInEx/BetterPerformance/biome-cache/<key>.bin` with a header, checksum and verified flag
  (`BiomeCacheStore`), written atomically, oldest evicted past the directory allowance.
- Prefix, `verified` mode only: a verified entry is deserialized through the game's own
  `Load(BinaryReader, Version.World)`, `world.m_biomeData` and `m_world` are set exactly as the
  native `TryLoadCache` did, and the native loop is skipped. Any failure runs native.

So a world costs three loads to pay off: store, reproduce-and-promote, serve. `shadow` mode
does the first two and never serves.

## Invariants

- Served bytes were produced by this game binary on this machine and reproduced identically on
  a later load under the same key. The module never invents or transforms a point.
- The key changes whenever the seed, the world identity, the game version, any
  `WorldGenerator` method's IL, a Harmony patch's fingerprint on one, or the mod set
  (excluding this plugin) changes. It does not change on a BetterPerformance release alone.
- `GenerateSectors` and everything after it run native on both paths.
- Any exception on the main-thread path disables the module for the process
  (`biome_cache_status=failed_<type>`); the worker's failures only count.
- The native `cache/` files are neither read nor written; `RemoveCache` still runs before us.

## Counters

Labels `biome_cache_status`, `biome_cache_mode`, `biome_cache_enabled`, `biome_cache_result`
(`verified_hit`, `miss`, `miss_unverified`, `stored`, `promoted`, `verified_match`, `mismatch`,
`key_failed`, `load_failed_*`, `store_failed_*`), `biome_cache_patch_fingerprint_fallback`
(`true` when some patch fell back to the plugin version for the current key). Gauges `biome_cache_hits`, `_misses`,
`_misses_unverified`, `_stored`, `_promoted`, `_mismatches`, `_key_failures`, `_load_failures`,
`_store_failures`, `_key_ms`, `_load_ms_max`, `_store_ms_max`. Read them beside
`loading_biome_GenerateBiomePoints_last`: a hit should replace ~4-5 s with the load time.

## Unproven

Nothing has been served in a real session yet. The first two loads of every world only store
and promote; the third is the first that can show a gain. A `mismatch` on a world that did not
change means the generator is not deterministic under some condition the key does not capture —
report it before enabling `verified` mode on that installation.
