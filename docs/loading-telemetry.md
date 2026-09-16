# Client loading timeline

The optional passive observer follows one latest client loading episode. It does
not move players, request network data, repeat readiness scans, save files or alter
loading decisions. It retains fixed counters and timestamps, never game objects,
addresses, world names or character names. Enable it before joining to observe the
earliest boundary; capture rotation preserves pre-capture observations.
Dedicated servers report this client timeline as not applicable. Disabling the
observer censors a pending episode; later observations cannot bridge that gap.

## Verified native boundaries

- `FejdStartup.TransitionToMainScene` entry: accepted scene transition request,
  **not** the user click, authentication completion or successful network connection.
- `Game.Awake` entry: game scene initialization begins. A missing transition
  boundary is explicitly a partial timeline.
- `Game.RequestRespawn(float,bool)` entry: first observed scheduled request.
  Native code can cancel and reschedule its delayed invocation, so this milestone
  includes any subsequent scheduling delay and is not proof of acceptance.
- `Game._RequestRespawn` entry: actual respawn work begins. Native code can save
  and destroy the previous player before setting the pending-spawn flag.
- `Game.FindSpawnPoint` successful return: native spawn-point readiness accepted.
- `Game.SpawnPlayer` successful return with the returned player equal to the local
  player: construction, profile loading and native spawn callbacks returned.
- `Game.UpdateRespawn` successful return with its request flag cleared after a
  successful observed spawn: includes native initial-spawn callbacks and resource
  collection checks. It does not mean all scene objects have loaded.
- `Hud.UpdateBlackScreen` successful return for the local player, with the native
  loading CanvasGroup alpha at or below zero and its object inactive: loading
  screen released. This does not establish a rendered frame or responsive input.

`m_firstSpawn` classifies initial joins; subsequent requests are labeled death
respawns or other respawns. Teleports do not create a join episode. Disappearing
game scenes and episodes unfinished after 30 minutes are censored. A newer episode
replaces the single retained timeline and counts unfinished replacement; rapid
episodes entirely between capture polls can therefore be absent from the file.

## Costs and waiting

Find-spawn, area-readiness (only inside find-spawn), player construction and
respawn-update costs are inclusive elapsed milliseconds, **not CPU time**. These
calls nest: never add them to estimate an exclusive total. An unsuccessful area
check can reflect a missing zone or missing valid object instances; the observer
does not infer which. Native readiness and fallback logic are unchanged.

The vanilla `Spawned after` log uses accumulated `dt` in `m_respawnWait` and only
covers part of loading. Wall milestone differences can include frame stalls,
scheduling, disk work, other mods and network waits. They cannot attribute those
causes by themselves. Minimum native respawn delay and ground/bed fallbacks are
included; the observer performs no extra game readiness calls.

## Capture interpretation and bounded overhead

`loading_sequence` identifies one process-local episode. All operation counts,
sums and maxima are cumulative **within that sequence**, not interval deltas.
Compare repeated snapshots by sequence; do not sum snapshots. Missing milestones
are unavailable, never zero. `loading_started_utc` correlates the timeline with
other logs; monotonic timestamps determine durations. Origin and coverage labels
identify partial starts. The fixed timeline has eight milestones and four operation
aggregates; scoped callbacks use constant work and no per-call object allocations.
Disabling observation censors the active episode. Re-enabling after its respawn
request already ran does not reconstruct the missing episode; the next observed
request or scene transition starts a new timeline. A manual capture stop suspends
this observer until a new capture or world session.
Global area-readiness and HUD hooks return immediately outside a tracked episode.
Instrumentation overhead still requires real-runtime measurement.

Standalone core tests cover waiting versus nested costs, duplicate milestones,
missing stages, sequence separation, clock regression, timeout and replacement.
Metadata-only game checks verify current native signatures/call relationships and
that observation hooks do not mutate parameters or initiate game operations.
Standalone CLR tests cannot establish Unity loading-screen behavior or a speedup.
