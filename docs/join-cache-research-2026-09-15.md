# Join-time cache regeneration research

Read-only study of the decompiled Valheim 1.0.12 sources and the 2026-09-15 23:25 UTC
host-client join capture. No game was launched and no cache file was read or written
by this investigation. Extends [native biome cache findings](native-biome-cache-research.md).

## Verdict

Both join-time regenerations share one root cause: **`ZNet.World.m_worldVersion` is `0`
on a client that joined a remote server**, and both native caches gate their *read* on
that field equalling `Version.World.DeepNorth` (41). The map cache is therefore
**write-only on every remote join**: the client regenerates 2048²-class world samples,
deletes the old files, writes new ones, and can never read them back. The biome cache
fails on the same gate. The highest-value, lowest-risk candidate is a plugin-owned exact
cache of the minimap textures (~6.3 s), because its inputs are a closed set and its output
is byte-comparable. The 8 s respawn floor is a separate, independent cost.

## Verified mechanism — minimap texture cache (Q1)

| Evidence | Finding |
| --- | --- |
| `Minimap.cs` `Update()` l.656-667 | Sole live call site: `if (!m_hasGenerated) { … if (!TryLoadMinimapTextureData(ZNet.World.m_seed)) { GenerateWorldMap(); } LoadMapData(); m_hasGenerated = true; }`. `ForceRegen()` (l.632) has **no caller anywhere in the decompiled tree**. |
| `Minimap.cs` `TryLoadMinimapTextureData` l.765-771 | First guard returns false when the 4 files are missing **or** `Version.World.DeepNorth != ZNet.World.m_worldVersion`. |
| `Version.cs` l.64, l.137-139 | `World.DeepNorth = 41`; `CachedMinimap.Original = 1`. |
| `ZNet.cs` `RPC_PeerInfo` l.1110-1115 | Client builds `m_world = new World();` then reads only name, seed, seedName, uid, `m_worldGenVersion` from the wire. **`m_worldVersion` is never sent and never assigned** — it stays at the enum default `0`. |
| `World.cs` l.42, l.58-60 | Parameterless ctor assigns nothing; `m_worldVersion` default `0`. Matches the biome-cache research (`World.LoadWorld` is the only writer). |
| `Minimap.cs` `GenerateWorldMap` l.1950-1986 | Per-pixel `worldGenerator.GetBiome(wx,wy)` + `GetBiomeHeight(...)` over `m_textureSize²`, then `SaveMapTextureDataToDisk`. First statement is `DeleteMapTextureData(ZNet.World.m_name)`. |
| `Minimap.cs` `SaveMapTextureDataToDisk` l.1989-2013 | Writes mask/biome/height buffers plus a meta file of `seed` (int) + `1` (int). **The client does write the cache** — unconditionally, every join. |

Consequences:

- **Not a local-worlds-only feature.** The write happens on any client with
  `FileHelpers.LocalStorageSupport.Supported`. Only the *read* is blocked, by the
  version gate alone. Seed is checked (meta byte 0-3); world name and UID are not.
- **Second defect, path mismatch.** `Minimap.Start` l.571-580 builds the cache paths from
  `ZNet.World.GetSaveDirectory(Local)`, which uses `m_worldName` — **empty string on a
  joined client** (`World.cs` l.20 initialiser, never assigned in `RPC_PeerInfo`). The
  delete path uses the static overload with `m_name`, the real name. Write and delete
  therefore target different directories on a remote join. VERIFIED in code; the
  resulting on-disk paths are REPORTED, not observed (no save directory was inspected).
- `m_textureSize` is `256` in code (l.232) but is a serialized prefab field. 6.3 s implies
  ~4.2 M samples, i.e. 2048². REPORTED, not verified — read it at runtime.

**Runtime confirmation of the gate.** `TimingHooks.cs:54` patches the exact signature
`private bool TryLoadMinimapTextureData(int)`, and the start record carries
`probe.MapTextureCacheLoad = enabled`. The 23:25:08 interval holds **one** sample, maxMs
**0.0058**. A 6 µs return cannot have opened, read or decompressed four files; it is an
immediate false return at the first guard. This is direct runtime evidence that the
`Version.World.DeepNorth != ZNet.World.m_worldVersion` gate rejected the cache before any
file work, matching the `m_worldVersion == 0` mechanism above.

## Verified mechanism — biome and Pregenerate (Q2)

`TryLoadCache` returned false for the same reason: the prior research established the
comparison is stored-header-version vs `world.m_worldVersion`, and the client's value is
`0`. Run D of that research directly observed stored `41` against client `0`. Even a
*true* result would not avoid work: `VerifyBiomeData` regenerates unless the world version
is 41, so a client can never reach the fast path. **Writing a header the native loader
accepts would require forging version 41 into a client whose real version is 0** — the
prior research already rules this out, and `TryLoadMinimapTextureData` proves the same
constant gates a second subsystem.

The safer route is a plugin-owned cache of `WorldGenerator.Pregenerate` outputs. Its
input closure is verified and small: `WorldGenerator..ctor` (l.198-226) derives everything
from `m_world.m_seed` and `m_world.m_worldGenVersion` via `VersionSetup` and a
`Random.InitState(seed)` sequence, and restores `UnityEngine.Random.state` on exit, so the
process RNG is unaffected either way. `Pregenerate` (l.252-258) produces exactly four
persistent containers: `m_lakes`, `m_rivers`, `m_streams` and `m_riverPoints` (the third
call, `PlaceStreams(isDN: true)` l.257, contributes only to `m_riverPoints` through
`RenderRivers` l.364 — its return value is discarded). `m_cachedRiverPoints` /
`m_cachedRiverGrid` are lazily derived and must be left in their reset state.

## The 8 s respawn floor (Q3)

`Game.cs` l.90: `public float m_respawnLoadDuration = 8f`. `FindSpawnPoint` l.541 requires
`m_respawnWait > m_respawnLoadDuration && ZNetScene.instance.IsAreaReady(logoutPoint)`.

The capture's 8.02 s means the timer was the last condition to become true: area readiness
was already satisfied when the floor expired, so **the tail of the join was timer-bound,
not data-bound**. The head was not. `UpdateRespawn(Time.fixedDeltaTime)` is called from
`FixedUpdate` (l.697), and Unity caps fixed-step catch-up at `Time.maximumDeltaTime`
(0.333 s by default), so a 6.3 s main-thread stall advances `m_respawnWait` by at most
~0.33 s. The floor does **not** absorb the stalls; removing a stall should return close to
1:1 wall-clock. Falsifier: log `m_respawnWait` together with the first frame at which
`IsAreaReady` returns true. If readiness precedes 8 s by margin M, then M seconds are
recoverable only by touching the floor, and the stall savings are additive to it.

## Prior art (Q4) — all REPORTED, none verified in source

- **FasterTeleportation** (LVH-IT, Thunderstore, **deprecated**): "shorten my loading times
  from 8 seconds down to 3 seconds". The 8→3 figure matches `m_respawnLoadDuration`
  exactly, so it is very likely patching that field. Author disclaims all testing beyond
  "multiple hours". No mechanism published.
- **Valheim Performance Optimizations** (ontrigger): ZNetScene streaming, burst terrain and
  threaded collision baking. Does not claim minimap or world-generation caching.
- **Better Continents** (Nexus 446): claims "biome loading 10x faster" and a minimap fix for
  multiplayer timeouts. It *replaces* world generation, so it is an invalidator for us.
- **Expand World Rivers** / **Riverheim** (JereKuusela, Riverheim_Dev): reconfigure rivers and
  streams. Direct invalidators of any `Pregenerate` cache.
- **ZenMap** clears its texture cache when map size changes between worlds — evidence that
  cache-keying on world identity is a real failure mode in this ecosystem.

No mod was found that caches `GenerateWorldMap` output or `Pregenerate` output. Searched
Thunderstore, Nexus and GitHub; absence here is weak evidence, not proof of novelty.

## Ranked candidates

| # | Candidate | Mechanism | Integrity invariant | Expected | Risk |
| --- | --- | --- | --- | ---: | --- |
| 1 | Minimap texture cache | Plugin-owned store; on `Minimap.Update` first pass, if key matches, load our 3 buffers and skip `GenerateWorldMap`; else run native and store after | Key = seed + `m_worldGenVersion` + game build + IL hash of `GetBiome`/`GetBiomeHeight`/`GetPixelColor`/`GetMaskColor` + loaded-mod set. Shadow mode first: run native, compare **all** bytes of mask, biome and height buffers | ~6.3 s | Medium. Wrong map is a silent, highly visible failure |
| 2 | Pregenerate cache | Serialize `m_lakes`/`m_rivers`/`m_streams`/`m_riverPoints` after the native ctor; on hit, reflectively populate and skip `Pregenerate` | Same key. Shadow mode compares every float of every river point in native order; `m_cachedRiverPoints` must stay null and `m_cachedRiverGrid` at (-999999,-999999) | ~2.6 s | Medium-high. Feeds terrain everywhere; a subtle mismatch corrupts terrain, not just a UI texture |
| 3 | Fix the client cache path | Set `m_worldName` from the peer-info name so write and delete agree, or point our own store at a name-keyed dir | Must not change any native save/load path; name alone is an insufficient key, seed must still be checked | 0 s alone | Low, but enables #1 cleanly |
| 4 | Respawn floor | Reduce `m_respawnLoadDuration` | `IsAreaReady` must remain mandatory; never bypass readiness | ≤ 8 s | High. Spawning into unready terrain; behaviour change, not an optimization |

Candidate 4 is a behaviour change, not a cache. It should not ship under a performance
flag without an explicit opt-in and a stated fall-through-the-world risk.

## Falsification on the disposable world

1. **Establish the gate.** One join, log `ZNet.World.m_worldVersion`, `m_worldName`,
   `m_name`, `m_textureSize` and the four resolved cache paths. Predicts `0`, `""`, the
   real name, `2048`, and paths under the worlds root rather than the world directory.
2. **Confirm the guard is the only rejection.** Already satisfied once (one sample,
   0.0058 ms). Repeat across joins: any sample above a millisecond means a file was read
   and a later check rejected it, which would change the diagnosis.
3. **Shadow mode, three joins.** Compute the key, run native generation, compare all bytes.
   Zero mismatches across three joins is necessary, not sufficient.
4. **Paired A/B, graphical client only.** The 0.4.2 headless harness **cannot measure this
   candidate**: `Minimap.Update` returns early when
   `SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null` (l.651), so `GenerateWorldMap`
   never runs headless. Any measurement must use a graphical client.
5. **Negative controls.** Change the seed, change `m_worldGenVersion`, corrupt one payload
   byte, truncate a file, add a world-generation mod. Each must miss and fall back to
   native generation, and each miss must be recorded.

## What not to do

- Do not force `m_worldVersion` to 41 on a client. It gates at least two native subsystems
  and the client's value is genuinely unknown, not merely unset.
- Do not write or rewrite the native cache files. Use a separate namespace.
- Do not key on world name. It is empty on a joined client and collides across servers.
- Do not accept a cache hit on seed alone. Generator version and generator code are inputs.
- Do not claim the full 6.3 s as recovered time. Decompression, `SetPixels32`, `Apply` and
  validation all cost time and have not been measured.
- Do not carry the 0.4.2 headless numbers into this candidate. Different code path.

## Sources

Decompiled Valheim 1.0.12: `Minimap.cs`, `WorldGenerator.cs`, `World.cs`, `ZNet.cs`,
`Game.cs`, `Version.cs`. Plugin: `src/BetterPerformance/TimingHooks.cs`.
Web: [FasterTeleportation](https://thunderstore.io/c/valheim/p/LVH-IT/FasterTeleportation/),
[ValheimPerformanceOptimizations](https://thunderstore.io/c/valheim/p/ontrigger/ValheimPerformanceOptimizations/),
[Better Continents](https://www.nexusmods.com/valheim/mods/446),
[ZenMap changelog](https://thunderstore.io/c/valheim/p/ZenDragon/ZenMap/changelog/),
[expand_world_rivers](https://github.com/JereKuusela/valheim-expand_world_rivers).
