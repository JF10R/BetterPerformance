# Fast-start loading research

Read-only analysis of Valheim 1.0.12 and isolated capture
`20260915T122110Z-5e4b37`, 2026-09-15. No production change, build or game launch
was performed for this investigation. The objective of 1–10-second joins is a
target, not an established feasible result or a measured optimization gain.

### The 1,130-instance plateau precedes near-object creation

| Capture elapsed at interval end | Interval | Instances | Near creation calls / inclusive sum | Preparation boundaries reached | Successful creations | Local-zone demand calls |
| --- | --- | --- | --- | --- | --- | --- |
| 16.082 s | 6.032 s | 1,130 | 117 / 0.604 ms | 0 | 1,128 | 51 |
| 22.112 s | 6.029 s | 1,130 | 113 / 1.327 ms | 0 | 0 | 47 |
| 28.210 s | 6.098 s | 3,614 | 90 / 1,024.103 ms | 86 | 2,484 | 42 |
| 33.729 s | 5.519 s | 5,738 | 98 / 994.911 ms | 98 | 2,122 | 44 |

The first two windows never reach the near creation gate. Current native
`ZNetScene.CreateObjectsSorted` begins with `IsActiveAreaLoaded()` and immediately
returns when false (`IL0000..000C`). Together with sub-millisecond aggregate near
costs and zero preparation observations, this strongly implicates that early
readiness gate rather than the 4 ms object budget. A dedicated outcome counter
would directly confirm it; existing hooks alone do not exclude a foreign prefix
skipping the method.

The 1,128 creations in the earlier window coincide with distant-creation work
(197.371 ms). They are not evidence of near-creation throughput. Increasing the
near allowance or rearranging candidates cannot help while the method exits
before building its candidate list.

Once near creation starts, 86 and 95 batches in the next two windows yield, so
creation service becomes a separate later bottleneck. The timeline reports
respawn started at 13,203.554 ms and spawn point ready at 36,602.815 ms from its
own origin: **23.399 seconds wall elapsed**. The native log's `Spawned after
22.96033` is accumulated game time. HUD release is observed at 37,616.742 ms
from the timeline origin. These clocks must not be substituted for one another.

Local evidence: `.qa/loading-fast-start-windows.json` and corresponding native
IL files under `.qa/loading-fast-start-*.txt`. The table uses interval counters;
loading milestones and operation counts are cumulative within a sequence.

### The two readiness regions differ

`ZoneSystem.IsActiveAreaLoaded` requires a zone root in `m_zones` for **every
sector in the full near simulation area**. Its non-classic radius test is strict
distance less than `(nearRadius + 0.5) * zoneSize`. The captured near radius is 5:
the native integer-sector test includes 97 sectors. This counts required roots,
not instantiated objects or terrain jobs.

`ZNetScene.IsAreaReady(position)`, called by `Game.FindSpawnPoint`, first requires
the target zone to be loaded. `IsZoneLoaded` checks both root membership and the
absence of pending loading objects in that zone. It then queries nearby ZDOs
using `SimulationDistance(1, 0, false)` and requires an instance for every valid
prefab ZDO in that result. A missing instance yields false. It does not require
all objects in the entire large view-distance area.

Consequently, spawn readiness depends on a smaller local object region, but
near creation is globally held until the larger near-zone-root region is ready.
This is a concrete dependency to measure and accelerate through native work;
it is not permission to force either readiness predicate true.

### Smallest first experiment: bounded extra local-zone service

Native `ZoneSystem.Update` calls CreateLocalZones at approximately a 0.1-second
game-time cadence. `CreateLocalZones` creates at most one zone per call: it pokes
the center first, then scans rows; the first successful poke returns immediately.
`PokeLocalZone` returns true only after SpawnZone succeeds and it inserts the new
ZoneData into `m_zones`. An existing zone only has its TTL reset to zero and returns
false. A false full call means no new zone root, although terrain/prefab requests
may have been queued. Exact evidence: CreateLocalZones `IL0007..0011`,
`IL0071..007B`, `IL00B1`; PokeLocalZone `IL0000..001C`, `IL0037..005E`.

A QA-only wrapper at **the existing CreateLocalZones callsite inside Update**
can test extra service without replacing zone readiness or generation:

- Always execute the native call once. Extra calls require a verified initial
  join, the live native waiting-for-respawn state, the same scene/world and the
  main thread. Do not use this for later death respawns or ordinary gameplay.
- Continue only after a true result, while a small total-call cap and soft elapsed
  allowance remain. Example starting bounds: four calls total and 4 ms measured
  from the first call. A single native call can exceed the allowance.
- Stop immediately after false. Repeating a false scan cannot promise progress
  before terrain/assets become ready and can consume CPU on the same queue.
- Return **any-success**, not the last result. Update uses the return value to
  suppress server ghost-zone work when local work succeeded.
- Do not repeat Update, UpdateTTL, prefab aging, server ghost generation or game
  time advancement. Repeated pokes reset the same loaded-zone TTL to zero; they
  do not advance TTL or release prefab references.
- Preserve native exceptions and use a recursion guard. Restore normal behavior
  on cancellation, world change, spawn completion, timeout or missing proof.

This is work scheduled sooner, not work eliminated. It may increase frame spikes,
queue pressure or host CPU competition. It has not been implemented or benchmarked
by this research stream. A 97-root cold region and one-success-per-native-cadence
create a pacing cost even when individual roots are inexpensive; terrain and
prefab latency can add more. Existing roots and changing reference positions mean
97 times the cadence is not a measured lower bound for every join.

### Why the worker and prefab gates must remain intact

SpawnZone asks HeightmapBuilder.IsTerrainReady before instantiation. If a location
is pending, it also calls PokeCanSpawnLocation; that starts or reuses its soft
reference load, refreshes its lifetime and returns its actual loaded state.
Neither predicate may be bypassed. IsZoneReadyForType separately blocks an object
while a higher native object type remains in that zone's loading list.

The terrain worker completed 231 then 73 jobs in the two plateau windows, using
1,689.347 and 480.422 ms inclusive worker service. TerrainSyncWait was only
0.208 and 0.156 ms in those windows. Thus the earlier proposed busy-poll backoff
is not supported as a significant fix for this capture. Jobs can include distant
terrain and different requests; job counts are not zone-root counts. Additional
workers or bypassing native readiness are not justified by these figures.

### Later experiments, after the root gate opens

**More initial-join creation service** is simpler than new ordering. Temporarily
raise the existing creation allowance under an independently verified initial-join
state while preserving count caps, readiness checks, the successful-creation floor
and shared near/distant clock. Revert at local-player/respawn completion, without
depending on HUD telemetry being enabled. Compare total join time and worst whole
batch/frame time; a longer frame during a loading screen can be a deliberate
tradeoff, but cannot be called a smoothness improvement without measurements.

**Readiness-region priority** is a second experiment, not the first plateau fix.
Native order is ObjectType descending, then distance squared ascending. Preserve
that type ordering and stably promote only candidates in the exact readiness
region *within each type*. Capture the actual IsAreaReady position from the
FindSpawnPoint scope; do not guess it from a failed out-position, character origin
or camera. Retain original membership, native per-zone type readiness and a bounded
fallback policy. This cannot move a nearby lower-type object ahead of a distant
higher-type object, and native distance ordering already favors nearby objects,
so the remaining benefit may be small. Cross-type promotion is a materially larger
semantic change and is not recommended as the first experiment.

### Evidence required before claiming faster joins

Count native active-area gate false/true, successful zone registrations, helper
extra-call time and false-stop reasons. Align them with first near preparation,
first successful IsAreaReady, PlayerSpawned and HUD release. Keep object/batch
counts, terrain service, process/main-thread CPU, memory and frame tails alongside
the timeline. Use the same isolated world/character for paired reconnect trials;
report cold and warm trials separately. Native save/readback and object-preservation
checks remain required. Do not convert the current 23-second wait into a projected
speedup, and do not promise a 1–10-second result before those trials.
