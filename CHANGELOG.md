# Changelog

### 0.4.9

- Verify every native contract against Valheim 1.0.14 (2026-09-17). That update disabled biome-data caching, so `AltBiomeWorldData.VerifyBiomeData` now clears the cache and regenerates instead of calling `TryLoadCache`; the loading-details contract follows the new path and fails if the cache branch returns. `ZoneSystem.CreateLocalZones` and `PokeLocalZone` kept their exact body sizes, 179 and 97 bytes, while their pinned raw-IL hashes changed, which is metadata-token renumbering rather than a logic change: the four initial-loading fingerprints are re-pinned for both roles.
- Fix the game-contract harness aborting on the first failed module and silently skipping every later one. Each module now runs in its own scope, prints `FAIL <module>`, and a single exit lists all of them. The previously hidden failure this exposed was the initial-loading fingerprint above.
- Fix a terrain-paint loss window in save coalescing. `BeforeSave` advanced the native paint-hash gate before the deferred write existed; if the flush threw, the module disabled itself but left the gate claiming the edit was written, so the next native paint-only `Save` early-returned and that neighbour's paint was lost on reload. The gate is now rolled back to its pre-batch value when a flush entry fails. Never observed firing (0 fallbacks over 104 batches and 63 deferred saves in the recorded sessions), but the option is enabled in production on both roles.
- Add the loot-visibility probe: three postfixes time a mined chunk's disappearance to its ore being created locally, splitting the network leg from the local-creation leg on one process's clock, with an eight-bucket histogram of the perceived delay. Attribution is by position and time because the game records no link between a destroyed hit area and its drops; an absent arrival means this process created the drop itself. Client only, bounded, and free when nothing is being mined.

### 0.4.8

- Add opt-in smelter catch-up budget: a transpiler bounds the per-call `Smelter.UpdateSmelter` catch-up loop (default 8 simulated seconds per call); the native accumulator already carries the remainder, so totals, fuel use and timestamps are identical while a 20 to 34 ms return-to-base spike spreads over frames. Only the smelter family iterates; the other stations are O(1) and untouched.
- Add opt-in dungeon spawn slicing on the client: `DungeonGenerator.Spawn` places every room in one frame (24 ms mean, 45 ms max in forest crypts); the module replays the same per-room placement under a per-frame budget, holds the prefab release until the last slice, falls back to the native loop when the player is inside the dungeon bounds, and drops its queue entry on destroy. `bp_dungeon on | off | status`.
- Add opt-in speculative map pre-compression: a worker thread re-encodes and gzips a main-thread snapshot of the explored bit arrays through the game's own writer and primes the exact compression cache, so the synchronous character save hits it instead of paying 60 to 87 ms of native gzip. Byte identity is enforced end to end; a stale snapshot is an ordinary miss.
- Add count-only diagnostics ahead of three further optimizations: clutter patch generation (two timings, six counters), build-mode events (`BuildMenuOpen`, `PieceRemove`, `PieceCopy` timings, snap and clipping counters, a 10 ms placement bucket) and a seven-way `ZDO.SetOwner` caller split with per-cycle release/reclaim counters on the server. Log the unresolved cosmetic prefab names.
- Add six research documents (2026-09-17) with the verified game mechanisms behind each item.

### 0.4.7

- Add opt-in GUI group-sound deduplication: `InventoryGui.SetActiveGroup` creates the group effect even when the requested group is already active, stacking a second sound on the button click for the Craft/Upgrade tabs and recipe clicks; a prefix clears `playSound` in exactly that case and leaves the cycling path and every real group change alone.
- Add opt-in mined-drop placement at the hit point: clear the public `m_hitEffectAreaCenter` prefab field on the listed `MineRock5` prefabs so drops and hit effects use the pickaxe impact point instead of the destroyed chunk centre, which stops buried chunks dropping ore underground. Owner-side field override; `bp_mining on | off | status` toggles it in a running session for an A/B.
- Fix the minimap texture cache reporting `foreign_generate_patch` at every join: the plugin's own `WorldMapGenerate` timing probe patches `Minimap.GenerateWorldMap` under the main plugin id and is now an accepted owner, so the cache stores and serves again. Every other owner is still refused.
- Add 57 game-contract checks read from assembly metadata for the two new modules, and the documentation covering both. Both are off by default and self-report their status.

### 0.4.6

- Add 52 gameplay-loop timing probes: inventory and chest GUI, container interactions and changes, building placement, minimap explore and large map, ships and carts, tree/rock/destructible/wear/character damage and destruction, attacks, loot drops, smelter spawns, MonoUpdaters batches (crafting stations, SFX, instance renderer, smoke, floating, ships, transform and animation sync), item slow updates and auto-stacking, player/HUD/clutter/water updates.
- Add 44 bounded gameplay counters: chest opens granted or refused in use, concurrent open conflicts on the owner, container changes and GUI-open frames, inventory moves and sizes, smelter catch-up sizes, placement ghost frames, pieces placed and removed, tree/rock/destructible events, attacks and hits, drops, minimap explore updates and fog applies, ship and SFX instance counts, item auto-stacks.
- Omit idle timing summaries from interval records: an absent metric name means zero calls. Records shrink even though the probe set grew.
- Add the "Gameplay" report section, research on a two-player shared chest for ValheimPlus (design, race analysis, prior art, test plan) and its implementation prompt.

### 0.4.5

- Add opt-in Steam Cloud write buffer sizing: `SteamCloud.WriteFile` allocates a 100 MiB scratch buffer per write, three times per character save; the transpiler sizes it to the payload with identical bytes written and strict IL contract checks. Cloud write counters always export.
- Add opt-in minimap texture cache with shadow verification: the native world-map generation (about 6.3 s per remote join, because the native caches never load when `m_worldVersion` is 0 on a joined client) is compared byte-for-byte before an entry is ever trusted; verified entries skip generation. Keyed on seed, generator version, game/plugin version, generator IL hash and loaded mods.
- Add opt-in replication cadence: per-prefab minimum resend interval for cosmetic prefabs (fish, crows, seagulls) on the send path, lossless because every send carries full state; opt-in bird velocity publication so remote clients extrapolate instead of lagging. Replication telemetry (resend-interval and distance histograms, forced-send inserts, byte-budget truncations) always exports.
- Add opt-in server owner-grant expedite: an owner change applied from an incoming update is force-sent to the new owner, mirroring the container-open path, to shorten the second player's pickup tail. Grant counters and the ZDO count gauge always export.
- Add opt-in terrain neighbour-save coalescing: one `TerrainComp.Save` per neighbour per paint operation instead of one per painted edge vertex, byte-identical saved state. Regeneration attribution by reason and per-frame histograms always export.
- Split allow-listed routed RPCs (damage, terrain operation, ownership request, container open) by target prefab in a new `routed_rpc_target` attribution group; add damage-number pressure gauges.
- Add session analysis, research and roadmap documents for the 2026-09-15 session, plus a self-contained ValheimPlus map-sync PR prompt. Diagnostics remain on by default; every optimization is off by default and self-reports its status.

### 0.4.4

- Add base-simulation timing probes: structural wear batch and support recomputation, heightmap late batch, regenerate/modifier/collision/render rebuilds, terrain operations, crop/smelter/fireplace/cooking/beehive/sap/fermenter/windmill ticks, location, vegetation, zone and dungeon spawns. Add live population counts for pieces, heightmaps, legacy terrain modifiers and slow-update objects.
- Add bounded per-name attribution (top 24 plus a conserved "other" row) for object creation cost by prefab, serialized replication bytes by prefab and routed RPC dispatch by handler name. New `attributions` array in interval and end records; absent in older captures.
- Read Unity engine markers and counters through `ProfilerRecorder`: present/render-thread waits, GC.Collect pauses, frame times, GPU frame time (separate switch), draw/batch/triangle counts, plus fixed-step-per-frame accounting and GC mode. Availability is reported per metric at runtime.
- Add read-only host facts (process priority, affinity, timer resolution, power scheme, OS, shared performance counter for client/server alignment), Steam transport path (direct or relayed, relay POP), online backend, peer socket types, ownership request and ZDO manager counters.
- Extend the offline report with base simulation, attribution, engine, host/network and ownership sections. Diagnostics only: no gameplay, save, ownership or networking behavior changed; every new probe is switchable and self-reports unavailability.

### 0.4.3

- Add opt-in initial client loading acceleration: bounded extra native zone passes, unchanged readiness, native contract guards and conservative patch fallback.
- Add per-join cumulative work/cost counters and configuration state to captures and offline reports. Preserve observations across capture rotation; do not infer saved time from extra work counts.
- Expose one `InitialLoading.Enabled` switch; dedicated/listen servers and later respawns retain their original scheduling.

### 0.4.2

- Add passive biome cache/generation stage timings and native spawn-wait reason counters, preserving pre-capture observations with bounded storage.
- Report process-cumulative loading details without double-counting snapshots or treating readiness checks as seconds saved.
- Investigate loading-only zone bursts in the isolated QA harness; this experimental acceleration is not included in the gameplay DLL.

### 0.4.1

- Add bounded passive client loading timelines from accepted scene transition through readiness, character initialization and observed loading-screen release. Preserve pre-capture coverage and distinguish partial or interrupted episodes.
- Attribute inclusive join, world-generation, terrain-worker/wait, minimap-load and resource-check costs. Separate monotonic wall duration from the native accumulated-game-time spawn log.
- Keep repeated timeline counters cumulative per episode in offline reports; never add nested costs or infer missing milestones.
- Harden map serialization fallback for custom streams and local package-copy ownership guards for patched constructors/Clear.

### 0.4.0

- Add optional exact map compression reuse with complete native-input equality, bounded retained memory, native contract guards and late-patch fallback.
- Add optional local replication-package copy removal without changing wire format, ordering or ownership; expose avoided payload-allocation volume.
- Observe private committed and Unity allocator memory, optional sparse render timings and bounded local pickup/container outcomes with explicit ambiguity and partial-transfer semantics.
- Time native sector discovery, client/server replication sorting and aggregate character fixed updates; report neighboring bottleneck intervals offline.
- Add opt-in creation allowance after leading near preparation, with conservative nested/distant fallback and separate preparation/service telemetry. Preserve native readiness, order and count limits.
- Omit entirely idle action categories with explicit schema/coverage labels to reduce export allocations and log volume.
- Evaluate bounded immutable compression workers on desktop runtimes. Keep worker/save/AI architecture changes outside the installed plugin until Unity lifecycle and behavior invariants are validated.

### 0.3.3

- Observe allowlisted local graphics settings at capture start, after graphics application and during regular polls. Separate raw player preferences, active settings and synchronized simulation distances.
- Export bounded field-change history with observation times, source, latest snapshots and overflow counts. Retain pending changes at final export; document sampling and segment-gap limits.
- Add finer inclusive save/RPC timing categories and passive object-budget diagnostics, including bounded observed post-yield waits, expensive individual creations and censoring.
- Add aggregate AI cadence, pathfinding/spawn timing and Windows main-thread CPU accounting; no per-NPC scans or changes to AI behavior.
- Add optional bulk minimap bit serialization with exact native payload format, bounded workspace and strict fallback guards. Retain native compression, pins and persistence lifecycle; see the runtime validation for measured scope.
- Clarify that loot queue observations do not count pickups or chest transfers. Extend reports and offline regressions for configuration changes.

### 0.3.2

- Keep every measured game duration while sampling recorder self-cost once per 64 records; remove repeated self-timing work from the hot path.
- Reduce default loot scans to 250 ms cadence, 128 candidates and a soft 0.2 ms budget. Add one-second cooldown after a scan exceeds 0.5 ms; avoid clock/scene work for untracked creations.
- Back off polling after complete collector work exceeds 2 ms, up to ten-second exports with gradual recovery. Preserve continuous timing aggregation and expose coverage/cost counters.
- Request below-normal export thread priority. Add allocation, accounting and backoff regressions; no new gameplay optimization or runtime performance claim.

### 0.3.1

- Add optional continuous capture with session/segment identity and a non-destructive directory allowance.
- Add bounded passive loot-queue observations independent of loot-priority enablement; distinguish local observed waits, opportunities and censored coverage.
- Add structured slow-operation summaries with configurable method/loop/worker thresholds and interval context; no per-call log spam.
- Expose local `bp_mark` and extend reports for segmented sessions, slow windows and loot coverage.
- Prepare a two-second, 30-minute-segment real-session profile without QA. Validate offline tests and current client/server IL; new telemetry overhead and long-session behavior await gameplay measurement.

### 0.3.0

- Add opt-in quota expansion under the existing soft creation-time budget.
- Add opt-in nearby-loot priority within vanilla object-type tiers; retain vanilla order every fourth eligible pass.
- Bound prefab classification and scratch storage; expose option activity and fallbacks in captures.
- Following renewed test authorization, validate offline regressions and a sixteen-window headless comparison. Budget plus quota reduces mean observed loot availability from 268.57 to 158.73 ms in this workload; incremental priority results remain mixed. Document uncertainty and safety limits; no package release.

### 0.2.0

- Add an opt-in soft time budget shared by near/distant scene object creation.
- Preserve vanilla readiness, priority, count limits and invalid-prefab behavior; guarantee progress past failed prefabs.
- Add a main-thread runtime toggle and capture counters for controlled comparisons.
- Add budget boundary/progress regression tests and document latency tradeoffs.
- Validate one continuous eight-window client/server comparison: lower client creation-batch peaks, modest loop changes, variable loot latency; retain default-off status.

### 0.1.1

- Fix unavailable Windows Unity/Mono working-set readings with native PSAPI; omit invalid values.
- Add native Steam pending/unacknowledged bytes, rate, ping and queue-time estimates, separate from BetterNetworking-adjusted socket values.
- Support the dedicated Steam interface and known ServerSync buffering wrappers without altering their behavior.
- Add network-peer, RPC, save-update, sorted-object and distant-object timing probes.
- Add bounded scenario markers and identify loop gaps crossing phase boundaries or spanning GC collections.
- Extend reports with native counters, memory validity and marker interpretation.
- Add a two- or three-block comparison report with run-level ranges and exploratory paired 95% uncertainty intervals.

Diagnostics only. No spawning, saving, replication, compression or bandwidth behavior changed. BetterNetworking remains a separate optional mod.

### 0.1.0

- Initial bounded client/server diagnostics, background JSONL export and offline reporting.
