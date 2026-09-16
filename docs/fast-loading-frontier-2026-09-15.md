# Frontier investigation: toward 1–10 second joins

Research target requested 2026-09-15. This is an objective, not a measured result
or promised release target. Preserve world output, persistence, native collision
and object-type readiness, ownership and missing-data handling.

### What a successful measurement means

Measure accepted scene-transition request to native local HUD release, with player
initialization and first responsive rendered input as separate endpoints. Existing
headless tests measure the first two but not responsive rendered input. Include
preparation performed before the measured boundary in a separate accounting of
total work: preloading moves latency, it does not make computation disappear.

Separate first connection to an unfamiliar world, reconnect after disconnect,
process restart with a valid disk cache, and reconnect while useful immutable
results remain resident. A one-second warm reconnect says nothing about cold
startup. Report process/server CPU, peak memory and frame tails alongside time.

### Concrete serialization barrier

Near object creation waits on IsActiveAreaLoaded. At the tested nonclassic radius
five, that requires 97 zone roots. CreateLocalZones returns after one successfully
registered zone; ZoneSystem.Update invokes it on its roughly 0.1-second cadence.
The observed 1,130-instance plateau occurs before the creation service budget can
act. This is a scheduling/dependency target, not evidence that all available CPU
threads must run more arithmetic.

The first isolated prototype repeats the existing local-zone function only after
success, at most four calls and a soft eight-millisecond budget per native call
site. It stops immediately when no zone is created and is restricted to initial
client respawn. Native location/terrain readiness, registration, ordering and
outer Update/TTL behavior remain. Individual calls cannot be safely interrupted.
It is confined to the QA harness until its behavior and benefit are measured.

### Architectures worth testing, in increasing scope

| Idea | Concrete mechanism | Required evidence / failure mode |
| --- | --- | --- |
| Bounded zone bursts | Remove one-success-per-update serialization during initial join | Real root progress, first near creation, wall loading, frame/server contention; stop without progress |
| Loading-only creation allowance | After roots are ready, temporarily give existing native object creation more service | Separate from root burst in experiments; preserve count/type/zone checks and restore at spawn, independent of diagnostics |
| Bounded terrain lookahead | Enqueue native terrain demand ahead of registration using a small window | Native ready queue holds only 16 results; blindly prefetching 97 roots can evict useful work and cause rebuilding |
| Exact derived-world cache | Persist immutable lake/river/biome derivations with complete validity and ownership checks | Include game/generator version, relevant configuration and patch compatibility; seed alone is insufficient; rehydrate current-world references |
| Server-prepared derived data | Reuse an already running server's exact deterministic generation outputs | Optional versioned protocol, bounded payload/decompression, complete identity/parity validation, native fallback; transmission plus validation must beat local generation |
| Deterministic parallel preprocessing | Build private arrays/components in workers, then apply results in original canonical order | Native flood-fill traversal/list ordering affects later shuffling/random choices; equivalence includes ordering and random-state effects, not only biome counts |
| Measured preload window | Prepare reusable assets while the user is selecting/joining | Include hidden preparation time, cancel stale work, cap memory; avoid activating or mutating a world before authorization |
| Explicit initial-data barrier | Replace heuristic waiting only after proving receipt and instantiation of the required initial state | Current IsAreaReady can inspect only known objects; absence of known missing objects is not proof all server data has arrived |

### Why exact output is a stronger requirement than a plausible map

Native VerifyBiomeData loads a cache, may regenerate points, then always generates
sectors. GenerateSectors builds connected regions and ordered biome/sector lists.
GenerateAltBiomes seeds UnityEngine.Random, shuffles sector lists, consumes random
values and applies modifiers. A parallel flood fill with the same connected regions
but different sector/list order can select different modifiers. Caching only the
visible terrain or replacing Unity.Random with a different generator is insufficient.

A candidate worker implementation should first produce private deterministic
intermediates, compare every relevant output/order against native, and account for
the main-thread reconstruction cost. Shared WorldGenerator/FastNoise state and
Unity objects cannot be concurrently mutated. The existing terrain builder already
uses a worker; more workers are not automatically useful.

### Readiness and minimum wait

The constructor's eight-game-second respawn delay is not a verified eight seconds
of removable wall latency. It overlaps loading and may allow replication to arrive.
Its active value and outcomes need observation. Never set IsAreaReady to true,
spawn above guessed terrain, or remove the delay just to improve a stopwatch.
The long-term alternative is evidence of initial-state completeness, with timeout,
reconnect/cancellation handling and native fallback when that evidence is absent.

### Engine constraints and prior-art checks

Unity asynchronous scene/resource loading already overlaps reading/deserialization
with main-thread integration. Loading priority changes the integration allowance;
it does not parallelize arbitrary managed initialization.
[Unity loading priority](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-backgroundLoadingPriority.html)

Unity jobs require suitable data ownership and dependencies. Global Unity.Random
state is unavailable for background use; exact native random behavior must be
preserved rather than substituting another algorithm.
[Unity thread-safe job data](https://docs.unity3d.com/6000.0/Documentation/Manual/job-system-native-container.html),
[Unity Random](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Random.html)

Holding scene activation at 90% is not a generally safe preloading shortcut:
Unity documents that an unactivated scene load stalls the async operation queue.
A retained-scene design needs actual lifecycle/resource evidence.
[Unity scene activation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AsyncOperation-allowSceneActivation.html)

No claim that these architectural ideas are globally novel, or that a particular
third-party Valheim mod already implements the complete approach, is established
by this investigation. The distinctive contribution here is the measured native
barrier and a testable route to removing its scheduling cost while preserving checks.
