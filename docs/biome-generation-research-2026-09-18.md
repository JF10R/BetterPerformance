# AltBiomeWorldData generation — can the plugin reduce its cost? (2026-09-18)

Question: can BetterPerformance safely reduce the cost of `AltBiomeWorldData` generation, and how?
Sources: `D:/GitHub/valheim-decompiled/assembly_valheim` (1.0.15), plugin `src/BetterPerformance`.
Verdict up front: **yes, via a plugin-owned cache of the point arrays only (option a)** — background
threading and frame slicing are unsafe here because the failure mode is silent wrong terrain, not a crash.

## VERIFIED in source

1. `VerifyBiomeData` is three unconditional calls — `RemoveCache(world.m_name)`,
   `GenerateBiomePoints(world)`, `GenerateSectors()` (`AltBiomeWorldData.cs:69-74`). It *deletes* the
   native cache file every load; `TryLoadCache` is unreachable from it.
2. `GenerateBiomePoints` (`AltBiomeWorldData.cs:99-131`): fixed 2048×2048 loop; per point calls
   `WorldGenerator.instance.GetBiome(x,y)` and `GetBiomeHeight(...)`, or short-circuits to
   `Ocean` / `-1000f` when `sqrMagnitude > 110250000f` (r = 10500). Writes only `PointBiomes` and
   `PointHeights`, sets `PointsGenerated`, then assigns `world.m_biomeData` (`:127`). 4.19 M points.
3. Determinism: `WorldGenerator` state comes from `m_world.m_seed` and `m_world.m_worldGenVersion`
   only (`WorldGenerator.cs:198-227`, `VersionSetup` `:238-250`). No Unity object, no texture, no time
   input in the point loop. So point output is a pure function of (seed, worldGenVersion, game binary).
4. The point loop is already proven thread-safe by the game: `HeightmapBuilder.BuildThread`
   (`HeightmapBuilder.cs:110-145`) calls `worldGen.GetBiomeSector(...)` and `GetBiome(...)` from a
   worker thread (`Build` `:147-160`).
5. `GenerateSectors` (`AltBiomeWorldData.cs:143-295`) is NOT pure: it ends in `GenerateAltBiomes`
   (`:296-345`) which calls `UnityEngine.Random.InitState` / `Random.Range` and `Sectors.Shuffle()`
   (`:298`, `:317-323`) — Unity main-thread API. It also allocates `PointSectors` (2048² references),
   a `BoolArray2D`, one `BiomePointCoordinate` per point into `Biomes[b].AllPoints`, and a second copy
   into `AllPointsAboveSeaLevel` for height ≥ 30 (`:131-137`, `:165-172`).
6. The `IsDiscovered` pass (`:259-284`) is dead: `MinZone` and `MaxZone` are both assigned from
   `sector.Min` (`:249-250`), so `for (k = MinZone.y; k < MaxZone.y; ...)` never iterates. Consequence:
   `ZoneSystem.instance` is never dereferenced inside `GenerateSectors`.
7. Cache-key evidence. `GetFilePath` = `<local save data>/cache/<worldName>_biomedatacache.bin`
   (`:498-509`) — the key is the **world name plus one `Version.World` int** written by `SaveCache`
   (`:519-521`) and checked by `TryLoadCache` (`:536-541`). No seed, no world UID, no worldGenVersion,
   no game version. The client's `World` in `RPC_PeerInfo` is built from the wire with
   name/seed/seedName/uid/worldGenVersion (`ZNet.cs:1109-1114`) and **never sets `m_worldVersion`**
   (field at `World.cs:41`), so the only guard reads a default-initialised enum.
8. `Load` restores `PointHeights`/`PointBiomes` and sets `PointsGenerated` only (`:558-573`) — sectors
   were always recomputed even on a cache hit. Serialised point = float + byte = 5 B (`BiomePoint.cs:12-22`),
   so a full file is 2048²×5 + 8 = **20.97 MB**.
9. Readers of the result, by phase:
   - join/critical: `HeightmapBuilder.Build` `:155-158` (terrain, worker thread);
     `Minimap.cs:2654` (world-map generation, same join); `Heightmap.cs:758`; `EnvMan.cs:566,691`;
     `WorldGenerator.GetBiomeHeight` `:1016`; `ZoneSystem.cs:1410`.
   - later: `ZoneSystem.cs:1913` (`GenerateLocations`, server, random point per location),
     `SpawnSystem.cs:622`, `SpawnArea.cs:198`, `Player.cs:2045`, `Terminal.cs:2444,2450-2570` (console).
10. The not-ready path is **silent and wrong, not fatal**: `GetBiomeSector` returns
    `BiomeSector.EmptyBlackForest` when `m_biomeData == null` and `EmptyMeadows` when `!IsReady`
    (`WorldGenerator.cs:838-846`). One reader would throw instead: `EnvMan.cs:705` dereferences
    `ZNet.World.m_biomeData.Biomes[...].Sectors[0]` unguarded.
11. Plugin assets that transfer directly: `MinimapTextureCache.ComputeKey`
    (`src/BetterPerformance/MinimapTextureCache.cs:308-333`) already hashes plugin version, game
    version string, seed, worldGenVersion, layout, an IL closure hash rooted at `WorldGenerator`
    `Initialize`/`Pregenerate`/`VersionSetup` + ctors (`Closure` `:275-303`, `ClosureHash` `:344-365`,
    which also folds in Harmony patch owners) and a mod-set hash that refuses to key on failure
    (`Plugins` `:367-378`). Shadow/verified modes and byte-compare live in the same file (`:51-93`).
12. `LoadingDetailsTelemetry` already patches all four stages by name —
    `VerifyBiomeData, TryLoadCache, GenerateBiomePoints, GenerateSectors` (`LoadingDetailsTelemetry.cs:40`,
    `Install` `:76-83`) — recording `Calls/Failures/SumMs/MaxMs/LastMs/StartedUtc` per stage (`Stage` `:58-63`).

## INFERRED (not stated by the code)

- What made the native cache invalid: item 7 is sufficient on its own. Two different worlds sharing a
  name — the common "Dedicated"/"world" case, one local and one remote — resolve to the same local
  cache file, and the only guard is a version int that is never set on the client. A hit then loads
  another world's biome grid: `GetBiomeSector` answers confidently with the wrong sector, terrain and
  spawn geometry diverge from the server's, and the join breaks with no error. That matches the patch
  note exactly and needs no other defect.
- Resident cost: `PointHeights` 16 MB + `PointSectors` 2048² references (~33 MB on 64-bit) +
  `PointBiomes` + the `AllPoints` lists (≥4.19 M × 4 B plus the above-sea-level copies) — an
  allocation profile that makes `GenerateSectors` plausibly GC-heavy, not just CPU-heavy. Unmeasured.
- The 4.6 s client figure covers `GenerateBiomePoints` only; `GenerateSectors` cost is unknown and may
  be the larger half.

## Options, ranked

1. **(a) Plugin-owned cache of the two point arrays.** Cache `PointBiomes`/`PointHeights` (the pure,
   deterministic part, item 3) under a `ComputeKey`-style key extended with the world **uid** and
   **seed** — the two fields the native key lacked — plus an IL closure rooted at `GenerateBiomePoints`,
   `GetBiome` and `GetBiomeHeight`. Prefix `GenerateBiomePoints`, keep `GenerateSectors` native so the
   Unity-bound and `ZoneSystem`-adjacent work is untouched. Shadow mode first: generate natively,
   compare byte-for-byte, store; promote to verified only after a previous run produced an identical
   grid under the same key. Worth up to the whole 4.6 s per remote join at ~21 MB/world on disk.
   Proof of safety = a shadow run whose stored and regenerated grids are byte-identical across a
   restart, plus a deliberate key-collision test (two worlds, same name, different seeds).
2. **(c) Frame slicing `GenerateBiomePoints`.** Safe on determinism (the loop has no cross-iteration
   state) and it converts a 4.6 s freeze into a responsive load, but total CPU is unchanged and the
   window during which `m_biomeData` is unset widens — every reader in item 9 must be shown not to run
   in that window. Lower value than (a), more invariants to hold.
3. **(b) Worker thread.** The point loop itself is thread-safe (item 4), but the consumers are the
   problem: `Minimap.GenerateWorldMap` and the first `HeightmapBuilder` batch both read biome sectors
   during the same join, and a miss returns `EmptyMeadows` silently (item 10). A lost race yields a
   wrong minimap and wrong terrain with no exception — and would poison `MinimapTextureCache` stores.
   Only viable behind a hard join-path barrier, which removes most of the overlap it was meant to buy.
4. **(d) Do nothing.** Defensible for the server (once per world load) but not for the client: 4.6 s is
   the largest single stage measured in the join, and (a) is a variant of machinery already shipped.

## First measurement, before any optimization ships

Enable `LoadingDetailsTelemetry` over a remote join and a local host load and record, per stage:
`LastMs` and `MaxMs` for `GenerateBiomePoints` **and** `GenerateSectors` separately (the split is the
whole decision), `Calls` per session (confirming once per join), managed heap delta across
`VerifyBiomeData`, and wall-clock join time. That is the baseline the A/B in item (a) is measured against.

## Unresolved questions

- What fraction of the stage cost is `GenerateSectors`? Unmeasured; decides whether (a) alone is enough.
- Does anything read `m_biomeData` between `WorldGenerator.Initialize` and the end of `RPC_PeerInfo`?
- Is `Heightmap.BiomeIndex`'s underlying type byte or int? Sets the in-memory and on-disk grid size.
- Does the dedicated server ever re-enter `VerifyBiomeData` (world reset / second load) in one process?
- Would a cached grid have to be invalidated by a mod that patches `GetBiome`, beyond what the existing
  mod-set hash already covers?

## Resolved after the report (lead, 2026-09-18)

The cost split was already in the 2026-09-17 isolated runs (`loading_biome_*`, both roles, one
call each): `GenerateBiomePoints` 4,986 ms on the dedicated server and 4,295 ms on the client;
`GenerateSectors` 693 ms and 610 ms; `VerifyBiomeData` 5,683 ms and 4,905 ms in total. The point
loop is ~88 % of the stage, so caching the point arrays alone (option a) targets ~4.3-5.0 s per
client join and per server world load, and leaving `GenerateSectors` native keeps the impure half
untouched.
