# Streaming and networking research: preserving High/Ultra

Research snapshot: 2026-09-14. Research only; no optimization was implemented or enabled, and no game was launched for this review. Existing test and capture results are context, not measurements of the options below.

The strongest candidates remove repeated work before adding more throughput: allocation-free package composition, then validated reuse of discovery/sorting if those stages are expensive. A fairer scheduler can improve latency distribution but does not remove the work. Background collision preparation has larger dependency and lifetime risks and needs separate evidence before a prototype.

All four options preserve the selected High/Ultra target object set. A smaller active area, permanently missing distant objects, reduced simulation accuracy, or longer unreported interaction delays would not count as success.

## What the recent prior art actually establishes

**Valheim Community Patch (VCP): distinguish the release history from stale indexed descriptions.** Version 0.26.1, commit [`c0ed4f72fbb803e049bf81319ceb84255231d31b`](https://github.com/MidnightsFX/Valheim-Community-Patch/commit/c0ed4f72fbb803e049bf81319ceb84255231d31b), removed Object Stream Rescan after multiplayer instance churn, missing newly generated distant objects, and interference with other mods' object lists. It retained the cached native spawn pass. The same release added collision safeguards for vehicles and location props. These are maintainer-reported regressions, not independently reproduced here. [Release notes](https://github.com/MidnightsFX/Valheim-Community-Patch/releases/tag/v0.26.1).

Version 0.26.0, commit [`5bffb7329ba7a71e968dbdce7f7bc8933b7e1cc0`](https://github.com/MidnightsFX/Valheim-Community-Patch/commit/5bffb7329ba7a71e968dbdce7f7bc8933b7e1cc0), removed its physics catchup and reflection patches because the maintainer found small gains and side effects. The release does not specify the affected physics cases or provide benchmark distributions. It therefore supports deprioritizing that approach, not concluding that every catchup cap is always harmful. [Release notes](https://github.com/MidnightsFX/Valheim-Community-Patch/releases/tag/v0.26.0).

The source inspected here is VCP head `c34081fc717721f5233e3778e87d10d786701f24` (September 13 Pacific time), newer than those release tags. Source inspection confirms the maintained cache and collider mechanisms below. No VCP code was copied into BetterPerformance.

**ValheimPerformanceOptimizations (VPO)** is useful algorithmic prior art, not a compatibility guarantee. Its current README describes rewritten streaming and threaded terrain collision, but explicitly says servers and Deep North were not tested. Its changelog records a 1.0.1 correction for object classification that allowed objects through floors, and older fixes for disappearing or incorrectly respawning objects after pooling changes. These historical defects do not prove that the current release retains them; they identify the lifecycle cases our validation must cover. [README](https://github.com/ontrigger/ValheimPerformanceOptimizations), [changelog](https://raw.githubusercontent.com/ontrigger/ValheimPerformanceOptimizations/master/CHANGELOG.md).

**BetterNetworking is already a separate baseline component.** The inspected fork revision is `72dd804419de47e3b7d131d4ce4399218e00aa45`. Its documented mechanisms include compression, connection buffering, and outgoing queue/update/send-rate adjustments. Compression requires participating endpoints; its own documentation identifies responsiveness tradeoffs from larger queues or reduced update rates. These are distinct from eliminating scene discovery work or packet-copy allocation. Its README references an older game patch and cannot establish current compatibility alone. [Pinned README](https://github.com/manchyy/valheim-betternetworking/blob/72dd804419de47e3b7d131d4ce4399218e00aa45/CW_Jesse.BetterNetworking/README.md), [queue adjustment source](https://github.com/manchyy/valheim-betternetworking/blob/72dd804419de47e3b7d131d4ce4399218e00aa45/CW_Jesse.BetterNetworking/Patches/BN_Patch_QueueSize.cs).

## Four distinct options

### 1. Reuse validated discovery and sorting results

**Mechanism and transfer.** VCP's retained near-object cache consumes a previously sorted list with a cursor, refreshes every third pass or on zone/list changes, and validates captured ZDO identity before use. It rechecks native type readiness before creation. This demonstrates a narrower alternative to replacing the whole event-fed object stream. Its pass-based refresh is approximately 100 ms at 30 Hz, not a wall-clock bound during stalls. [Pinned cache source, lines 44–118](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/c34081fc717721f5233e3778e87d10d786701f24/ValheimCommunityPatch/Patches/Performance/SpawnQueueCachePatch.cs#L44).

For server replication, Epic's Replication Graph provides a separate systems example: persistent spatial lists share candidate-gathering work across frames and connections. The transferable idea is reuse of query results; importing Unreal's graph or changing Valheim's relevance rules is unnecessary. Its large-player examples are not performance evidence for this two-player LAN. [Epic documentation](https://dev.epicgames.com/documentation/unreal-engine/replication-graph-in-unreal-engine?lang=en-US).

**Expected scope:** potentially material during large streaming backlogs or expensive per-peer list construction; small if time is spent instantiating, simulating, or waiting elsewhere. A discovery stage occupying 10% of measured CPU time gives at most a 10% CPU-time reduction if eliminated entirely. Frame-tail improvement requires separate measurement.

**Invariants:** preserve native near/far membership, type/readiness rules, ownership and destruction; invalidate for settings changes, teleports, moving ZDOs, receipt/creation/removal, zone completion, disconnect and scene replacement. Validate IDs because pooled ZDO references can be recycled. Preserve observable lists for other mods. For replication, retain per-peer revision/acknowledgement and forced-update semantics.

**Hidden cost/risk:** invalidation misses can hide new loot or distant trees, resurrect obsolete entries, or retain stale collisions. Periodic repair limits how long some defects persist but does not make them correct. Repeated rebuilds under movement can remove the gain. VCP's replacement prefix returns false: our current budget/priority transpilers inside the original `CreateObjectsSorted` body would not execute. Installing both is not a valid combined implementation.

**Falsifying experiment:** on a temporary world, compare cached candidate membership against native discovery in a diagnostic shadow path at controlled checkpoints: stationary base, continuous zone-edge movement, High/Ultra switches, teleport, new generation, loot creation, destruction and reconnect. Any unexplained membership difference fails correctness. Reject performance value if isolated gather/sort cost is small or rebuild frequency approaches vanilla while arrival latency rises.

**Telemetry:** current `SyncListBuild`, `SendZdos`, `ObjectCreateSorted` and `DistantObjectCreate` provide broad stages. Add bounded gather/filter/sort costs and counts, cache hit/rebuild reasons, invalid-ID rejects, and sampled oldest waiting age by native tier and near/far category. `ObjectCreateSorted` currently includes actual creation, so its duration alone is not sorting cost. Shadow full scans belong in QA, not continuous production logging.

### 2. Prepare expensive collision data in parallel, publish only when safe

**Mechanism and transfer.** Unity explicitly permits `Physics.BakeMesh` on worker threads, while prohibiting concurrent bakes of the same mesh. Reuse requires matching cooking options and unchanged geometry; creating and assigning components remains a main-thread operation. [Unity API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.BakeMesh.html).

VCP confines asynchronous baking to selected client terrain paths. It keeps synchronous paths for immediately required collision, waits before mesh mutation/destruction, and drains pending work for `Heightmap.ForceGenerateAll`. Its worker wait uses a loop yielding the thread: worker pickup or completion can still return as a main-thread stall. This is evidence of the necessary lifecycle complexity, not proof that its current implementation covers every modded object. [Pinned collider source](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/c34081fc717721f5233e3778e87d10d786701f24/ValheimCommunityPatch/Patches/Performance/AsyncColliderBakePatch.cs#L7).

**Expected scope:** exploration and terrain-boundary spikes if collision cooking is a major serial stage. Little expected benefit for a stationary dedicated server or GPU-limited rendering. High/Ultra still requires all collision dependencies; parallel work must finish before their consumers use them.

**Invariants:** exclusive mesh-job ownership; immutable geometry during bake; matching options; cancellation/lifetime handling; safe completion on scene teardown; publish collision before physical objects, vehicles or ground-snapped props depend on it. Keep a correct synchronous fallback. Do not move arbitrary Unity APIs to background threads or change ownership to hide incomplete simulation.

**Hidden cost/risk:** physics can advance while a required collider is absent; holding objects for readiness instead causes appearance/interaction delay. Blocking on a saturated shared ThreadPool moves rather than removes a hitch. Extra copies/jobs may increase total CPU and memory. The already-observed floating non-owner loot in QA cannot validate collision safety, and terrain correction can conceal penetration.

**Falsifying experiment:** compare warm loading and fresh generation separately while two clients cross/teleport across terrain boundaries; include locally owned falling loot, ships, carts, placed props, terrain edits and unload/reload. Any dynamic object admitted without its required collider, incorrect landing, or lost quantity fails. Reject the optimization if bake waits/assignment restore the same frame tail or interaction delay increases beyond the predeclared margin.

**Telemetry:** add sampled collision rebuild/cook/assignment elapsed time, queue-to-start delay, pending count/oldest age, forced synchronous waits and reasons, and readiness-to-publication latency. QA must record collision readiness, actual owner, gravity, contact/position and legitimate stack merges; IDs disappearing alone are insufficient. Current aggregate active-area readiness does not establish per-object collision readiness.

### 3. Make service fairer within existing dependencies

**Mechanism and transfer.** Glenn Fiedler's state synchronization design accumulates priority across updates and resets it only when an object is actually sent, so continuously important objects do not permanently exclude quieter ones. That is an explicit fairness mechanism. It does not establish a Valheim tuning value or a hard delay bound. [Original article](https://gafferongames.com/post/state_synchronization/).

For BetterPerformance, the narrower candidate is age-aware service within a native creation tier, retaining terrain/solid dependencies and some vanilla ordering. Server-side packet scheduling is a separate experiment and only justified if queue/serialization evidence supports it. No extra send rate or reduced target distance is required.

**Expected scope:** improve tail latency/fairness under competing ready work; not inherently reduce total CPU. The current 3-priority/1-vanilla-pass policy is a simple fairness provision, not a measured per-object delay guarantee.

**Invariants:** never promote physics-dependent loot ahead of required terrain/solids; preserve updates that convey ownership, creation, destruction and state changes; do not confuse observation age with time spent ready. Keep original protocol/order dependencies and a bounded fallback for unclassified objects. Reset age only after actual service; stale identity cannot inherit another object's age.

**Hidden cost/risk:** improving loot at the expense of doors, enemies, distant structures or larger state updates merely redistributes lag. Pure cost-based shortest-job selection can starve expensive objects. A nonpreemptible creation can exceed any soft budget, and admission age cannot prove the budget caused the delay.

**Falsifying experiment:** balanced repeated windows with mixed same-tier loot/non-loot, large objects and movement; measure every relevant class, not just wood. Reject if p95/p99 loot improvement is offset by unacceptable competing-object tails, sustained backlog growth, extra creation churn, or ownership/action regressions. Repeat distinct sessions; adjacent windows are not independent players or worlds.

**Telemetry:** use the new bounded `budget_deferred_*` and `budget_post_yield_*` observations plus individual creation costs. Add sampled readiness outcomes and service age by tier/category only if needed. Record censored/capacity-skipped samples. Existing post-yield time is an observed lower-bound interval, not causal added latency; it cannot compare budget-on with budget-off by itself because budget-off produces no budget-yield cohort.

### 4. Remove temporary nested-package copies without changing the wire format

**Mechanism and evidence.** VCP, crediting ComfyMods/Compress, writes a nested package's length and existing buffer directly instead of allocating an intermediate array. This is allocation removal, not end-to-end zero-copy or a compression change. [Pinned source](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/c34081fc717721f5233e3778e87d10d786701f24/ValheimCommunityPatch/Patches/Performance/ZPackageWriteAllocPatch.cs#L4).

Read-only IL inspection of the installed Valheim 1.0.12 assembly confirmed `ZPackage.Write(ZPackage)` calls `GetArray()` before writing the length and bytes. The inspected assembly SHA-256 is `27A766A8D23A7BD8B6A54FB9AD0452A96C305FB3629B39C40527C09A1C393A84`. This identifies an existing copy path; it does not measure its frequency, size or contribution to hitches. BetterNetworking's inspected Steam compression path operates elsewhere and still has its own queue/array work. [Pinned compression source](https://github.com/manchyy/valheim-betternetworking/blob/72dd804419de47e3b7d131d4ce4399218e00aa45/CW_Jesse.BetterNetworking/Patches/Compression/BN_Patch_Compression_Steamworks.cs#L11).

**Expected scope:** relatively small implementation surface and verifiable semantics; lower allocation pressure when nested packages are frequent/large. It cannot fix a long non-network simulation call or guarantee fewer pauses in a session whose hitches are not GC-related.

**Invariants:** identical bytes, length framing, source/destination stream positions, flush semantics and exception behavior for supported inputs. Retain snapshot behavior when source and destination alias; use a safe fallback for inaccessible buffers, self-write/aliasing and unknown package implementations. Never retain a mutable buffer beyond the original synchronous write lifetime.

**Hidden cost/risk:** buffer aliasing or later reuse can corrupt packets; a smaller allocation count with larger retained buffers may increase memory. Protocol neutrality must be tested with the actual compression/mod combination and both peers. Do not equate lower managed allocation with lower latency without measurement.

**Falsifying experiment:** offline byte-for-byte comparisons across empty, nested, nonzero-position, reused, large and aliased packages, then isolated mixed-version/BetterNetworking network QA. Reject correctness on any unexplained output/position difference. Reject performance value if byte volume/call count is negligible or copy removal is dwarfed by compression, transport waits or unrelated game work.

**Telemetry:** sampled call sizes and copy-stage elapsed/allocation observations; aggregate calls/bytes and actual GC pauses, not packet contents. Current `SendZdos`, `IncomingZdoData`, `RpcDispatch` and Steam queue observations help locate the broader stage. They do not isolate package copies or compression cost. Keep production observations bounded and disable additional probes once the question is answered.

## Selection and confidence

1. Package-copy removal is the smallest candidate with a strong offline equivalence test. Measure path volume first.
2. Validated discovery/sort reuse offers more structural upside if `SyncListBuild` or isolated scene gathering is substantial. The recent rescan rollback makes shadow equivalence and mod coexistence mandatory research gates.
3. Fair service is relevant when deferred work is ready but competing queues have poor tails; treat it as a latency tradeoff.
4. Collision preparation remains a conditional, higher-risk experiment until cooking is demonstrated to dominate exploration stalls.

Physics catchup caps are outside this shortlist. Unity's `maximumDeltaTime` also clamps game time/delta time and the fixed-step catchup count, so reducing it changes temporal behavior rather than merely speeding the same work. [Unity API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Time-maximumDeltaTime.html).

The last real session does not identify any of these as its proven cause. In particular, broad nested timings cannot be summed as independent CPU costs; low average CPU and sparse Steam queue snapshots do not rule out short stalls. The measured UI view-distance transitions were not reliably represented by the old captured zone labels, so no previous interval can be confidently labeled High versus Ultra from those fields alone.

Before a gain claim, hold the selected distance and final object population constant, separate startup/saves/steady exploration, report both frame and action/appearance latency, and count missing/censored observations. Require confidence intervals across repeated sessions or paired blocks, and publish losses as well as gains. Define acceptable latency regression margins before observing the comparison.

VCP identifies itself as GPL-3.0 and VPO as MIT. These references establish prior art; they do not authorize transplanting GPL implementation into a differently licensed project. Prefer independently designed changes around verified game contracts, or explicitly resolve licensing and attribution before source reuse. [VCP license](https://github.com/MidnightsFX/Valheim-Community-Patch/blob/c34081fc717721f5233e3778e87d10d786701f24/LICENSE), [VPO license](https://github.com/ontrigger/ValheimPerformanceOptimizations/blob/master/LICENSE).

## Local evidence pointers

- [TimingHooks.cs](../src/BetterPerformance/TimingHooks.cs), lines 17–74: current broad scene, replication, RPC and readiness boundaries.
- [ObjectCreationBudget.cs](../src/BetterPerformance/ObjectCreationBudget.cs), `Continue`, `ContinueWithTelemetry`, `AfterCreation`, `SampleTelemetry`: cooperative gate and observed deferred-work costs.
- [Budget telemetry semantics](budget-telemetry.md): bounded cohorts, censoring and limits of causal interpretation.
- [LootQueueTelemetry.cs](../src/BetterPerformance/LootQueueTelemetry.cs), `Observe` / `Created`: sampled native-queue visibility; not a count of all loot or inventory actions.
- Real-session source capture analysis exists locally under `.qa/analysis-real-server-20260915.md`; it is not a public repository artifact.

Working-tree references above describe the source inspected during this review and may move as telemetry development continues. No new benchmark is implied by this document.
