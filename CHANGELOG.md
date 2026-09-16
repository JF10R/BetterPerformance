# Changelog

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
