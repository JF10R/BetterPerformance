# Native biome cache findings

Read-only investigation of the locally installed Valheim 1.0 assemblies,
2026-09-15. No cache headers, world versions, generation behavior or save files
were changed by this investigation.

## Verified native behavior

| Method | Evidence and implication |
| --- | --- |
| `AltBiomeWorldData.GetFilePath(World)` | IL `0001` reads `World.m_name`; the string overload appends `_biomedatacache.bin`. The filename does not include seed, UID or generation version. |
| `AltBiomeWorldData.GetCachePath()` | Uses `Utils.GetSaveDataPath(FileSource.Local)` plus `cache`. Client and server can therefore share the same file when their save-data directory and world name match. |
| `AltBiomeWorldData.TryLoadCache(World)` | Checks local-storage availability and file existence. Reads and discards the first header integer; compares the second integer with `world.m_worldVersion` at IL `0040–0045`. No seed or UID validation. An exception is not a normal false result. |
| `AltBiomeWorldData.VerifyBiomeData(World)` | Always attempts the cache. At IL `0008–0017`, regenerates if biome data is absent **or world version is not 41**. Calls `SaveCache` after regeneration, then always calls `GenerateSectors`. A cache hit alone does not avoid generation. |
| `AltBiomeWorldData.SaveCache()` | Writes header integer zero, then the current `m_world.m_worldVersion` at IL `0043–0048`, then the point payload. It does not normalize the version. |
| `World` constructors / `ZNet.RPC_PeerInfo` | Constructors leave `m_worldVersion` at its zero default. The observed client peer-info path supplies name, seed, UID and world-generation version, but does not supply the world-save version. |
| `World.LoadWorld(SaveWithBackups)` | IL `021B` assigns the world version read from saved metadata. This is the only writer found in the installed assembly's `m_worldVersion` reference scan. |
| `WorldGenerator..ctor(World)` | Reads `m_worldGenVersion` into its generator version; does not update `m_worldVersion`. These are distinct values. |
| `AltBiomeWorldData.GenerateBiomePoints(World)` | Generates the 2048 × 2048 point grid; assigns `world.m_biomeData` and `biomeData.m_world` at IL `0119` and `0120`. Does not change the world version. |

`AltBiomeWorldData.Load(BinaryReader, Version.World)` forwards its version to
`BiomePoint.Load`. In the current implementation, `BiomePoint.Load` never reads
that parameter: every point is a single-precision height followed by one biome
byte. The matching writer uses the same five-byte layout. The complete current
file is 12 header/size bytes plus `2048² × 5` bytes: **20,971,532 bytes**.
Identical decoding today does not establish compatibility with other game builds.

## Runtime evidence

One isolated run observed approximately **4.17 seconds** in
`GenerateBiomePoints`, with a normal false return from `TryLoadCache`.
Follow-up run C directly confirmed client world version **0**, local storage
allowed, the native cache path existing, and a false cache result. The subsequent
run D directly observed header format **0**, stored world version **41**, client
world version **0**, and a false cache result. This confirms the version mismatch
at the native read in the isolated shared-directory setup.

The shared-directory hypothesis is a version cycle: a server loading saved
version 41 rejects a previous client header 0 and writes 41; a client with version
0 then rejects that file and writes 0. This can leave the same header and file
hash before and after an entire test despite an intermediate overwrite. It is
consistent with the code and the observed 41-to-0 read mismatch. The tests did
not trace every individual server file write; normal sessions may use separate
directories and must be measured independently.

Separately, read-only inspection of the first 12 bytes of two normal, non-QA
cache files found header format **0**, world version **0**, size **2048**, and
length **20,971,532** in both. No world identifiers are included here. This supports
the presence of client-version-zero caches outside the QA fixture; it does not
prove that normal sessions experience the same alternating writes.

Even a true cache result for version 0 would still trigger native regeneration.
The behavior is therefore not explained solely by a shared QA folder. Whether
the client-version rule is an oversight or intentional protection is unknown.

## Unsafe shortcuts

- Do not edit cache headers or force `m_worldVersion` to 41. Other native code,
  including `Minimap.TryLoadMinimapTextureData`, also uses that version gate.
- Do not accept point data solely because the filename or binary format matches.
  Two servers can expose same-named worlds with different generation inputs.
- Do not skip native verification or sector generation on the strength of a cache
  hit. Sector generation performs additional flood, neighbor, discovery and
  alternate-biome work.
- Do not claim the entire measured generation time is recoverable loading time.
  Replacement reads, allocation, validation and downstream work still cost time.

## Independent cache: shadow validation first

A separate cache could store the exact output of a completed native point
generation, with its own namespace and integrity checks. It must leave native
cache files, world versions, persistence and later sector generation unchanged.
Before any generation bypass:

1. Bind entries to the exact game/generation implementation and validated world
   identity and inputs: seed, generation version, relevant settings/mod state and
   any pre-existing biome state that affects generation. A name-only key is
   insufficient.
2. In shadow mode, still run native generation and compare all height bits and
   biome bytes with the candidate payload. Validate dimensions, payload length,
   integrity and fresh-container state; never reuse mutable sector objects.
3. Verify side effects and random-state parity. `GetBiomeHeight` reaches
   `GetBiomeSector`, which distinguishes absent biome data from present-but-not-ready
   data. Height generation also touches mutable river-cache fields. Full reachable
   RNG/state equivalence has **not** been established.
4. Test repeated joins, different seeds with the same name, both client/server
   roles, cache misses/corruption and supported mod configurations. Any unknown
   input, incompatible patch or failed validation must retain native generation.
5. Only after parity is established, measure paired warm/cold end-to-end loading
   with uncertainty and account for cache I/O, memory and validation overhead.

No independent generation cache or cache bypass is implemented by this report.
