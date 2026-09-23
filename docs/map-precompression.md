# Speculative map pre-compression

Optional, default off. Compresses the character map payload on a background thread while the
player explores, and primes the exact compression cache so the synchronous character save
reuses the result instead of running gzip inside `Game.SavePlayerProfile`.

Motivated by the pre-compression research: the
whole map cost sits on the synchronous save, `Minimap.SaveMapData` has exactly one caller and
`Minimap.GetMapData` exactly one, so priming the cache reaches every map save there is.

## Mechanism

1. **Dirty signal.** Harmony postfixes on `Minimap.Explore(int, int)` and
   `Minimap.ExploreOthers(int, int)` increment a counter when they return `true`; a postfix on
   `Minimap.ResetAndExplore(BitArray, BitArray)` bumps it for the wholesale rewrites that
   `SetMapData` and a map load perform.
2. **Trigger.** `Pump()`, called from the plugin's `Update`, reconsiders at most four times a
   second. It combines the explore counter with a fingerprint of the saved pins and the
   reference-position flag, and asks the policy whether to dispatch.
3. **Snapshot** (main thread). `BitArray.CopyTo(int[])` twice for `m_explored` and
   `m_exploredOthers`, `m_textureSize`, the saved pins flattened into plain fields, and
   `ZNet.IsReferencePositionPublic()`. The pin author is read reflectively and converted to a
   string here, because the native writer only ever uses its `ToString()`.
4. **Worker.** A background thread re-issues `GetMapData`'s inner-package writes in the same
   order. When the verified fast bool writer is available, packed snapshot bits are expanded
   into the same one-byte-per-bool representation in bounded blocks; otherwise it uses the
   native bool writer. It then calls the game's own `ZPackage.WriteCompressed`, which is
   `Utils.Compress` plus a length and a payload write.
5. **Publish** (main thread). The next pump adopts the `(input, encoded)` pair into the exact
   cache by reference.
6. **Save.** `Minimap.GetMapData` runs as usual; the cache compares the complete serialized
   input and, on equality, writes the pre-computed bytes. A finished worker result can also
   be adopted here, before the next pump, only if its full input matches the save. A stale
   pending result cannot replace a valid cache entry on this path.

## Thread model

Nothing the worker touches is owned by the game. `ZPackage` is a `MemoryStream` plus a
`BinaryWriter`, and the compressor is `GZipStream` over a `MemoryStream`; no Unity API is
called off the main thread. The snapshot and the publication both happen on the main thread,
so `ExactByteCache` keeps its single-thread model. The save path polls the publication lock
with `Monitor.TryEnter`: if it is busy, native compression remains available without waiting
for the worker. It compares the immutable input outside that lock, then revalidates the
publication references before removing the pending result. Worker publication and policy
completion are atomic with respect to adoption. The worker runs at below-normal priority
with `IsBackground = true`.

## Invariants

- Correctness never depends on the worker. The cache compares all of the serialized input
  before it emits anything, so a stale or wrong speculation is a miss and the native path runs
  and produces exactly what it produces today.
- At most one snapshot is in flight, and its result must be adopted before another run starts.
- A snapshot carries the `ZNet` instance it was taken from; a publication from a previous
  world is refused.
- The module requires `MapSaving.ExactCompressionCacheEnabled`. Without it the status is
  `unavailable_cache_disabled` and nothing is patched.
- A changed game layout (explore signatures, map fields, pin fields, package contract) fails
  installation closed. Any error inside the pump disables the module and leaves native
  compression in place.
- Adoption transfers ownership of both arrays; the module never mutates a published array.
- A dedicated server never saves a character profile, so the pump returns before any snapshot
  there; the module still reports `installed` on a server but runs nothing.
- The counters are process-cumulative like the cache's own; only the two maxima restart per
  capture. Read them as totals, not per-interval deltas.
- The on-disk format is untouched. The map version, the writer order and the compressed bytes
  are the game's own.

## Configuration

`[CharacterSave]`:

| Key | Default | Meaning |
|---|---|---|
| `SpeculativeMapCompressionEnabled` | `false` | Installs the module. Requires restart. |
| `SpeculativeMapQuietSeconds` | `2.0` | Stillness required before a run is dispatched. |
| `SpeculativeMapMinimumIntervalSeconds` | `30.0` | Minimum gap between two runs. |
| `SpeculativeMapMaximumRunsPerMinute` | `2` | Rolling-minute hard cap. |

The cap matters: each run costs roughly one core for the duration of one map compression, so
an unbounded trigger is the realistic way to turn this into a net loss on a weak machine.

## Telemetry

Gauges `map_precompress_runs`, `_skipped_dirty`, `_skipped_inflight`, `_snapshot_ms_max`,
`_worker_ms_max`, `_published`, `_primed_hits`, `_stale`, `_failures`, `_save_adoptions`,
`_bulk_runs`. The last two count results adopted directly during a save and workers using
the bulk bool writer. `map_cache_lookup_ms_total` includes save-time adoption and its exact
input comparison as well as the cache lookup. Labels
`map_precompress_status`, `map_precompress_enabled`, `map_precompress_policy`.

`_primed_hits` and `_stale` come from the cache's own single entry, which is marked when a
speculation is adopted and unmarked by the first lookup that resolves it, so no hit is counted
twice and a primed hit is also one ordinary `map_cache_hits_total`.

## Memory

One snapshot (two `int[]` backing copies, about 1 MiB at texture size 2048) plus the worker's
staging: the inner package's stream, one copy of the 8.4 MiB serialized payload, and the
compressed output. `GetArray()` copies, and the worker calls it twice, so budget a transient
peak above the research's 18 MiB estimate. The published pair counts against the cache's
existing 24 MiB bound and replaces whatever the cache held.

## Verification

Offline, in `tests/BetterPerformance.Tests`: the trigger policy (quiet window, unchanged
state, minimum interval, rolling-minute cap, reset) and the in-flight guard, plus adoption in
`ExactByteCacheTests`.

Offline, in `tests/BetterPerformance.GameTests`: the hooked signatures; the map,
pin and reference-position field contracts; that `Minimap.SaveMapData` and
`Minimap.GetMapData` have exactly one caller each, counted over the whole game assembly; the
`WriteCompressed`/`Utils.Compress` shape; the native inner-package writer order read off
`GetMapData`'s own IL; byte identity of the module's encode against that order on synthetic
data; and an end-to-end adopt-then-save that compares the served bytes against native
compression of the same input, including stale and disabled cases, adoption before the
next pump, and a worker holding the publication lock while a save proceeds. Core tests cover
packed-bit boundaries and byte identity. These checks do not measure Unity session gains.

## Unproven

The hit rate. Everything above is enforced or read offline; whether a quiet window of two
seconds and a cap of two runs per minute actually catch the save is a question only a Unity
session answers, and `map_precompress_primed_hits` against `map_cache_misses_total` is the
measurement. Snapshot and worker costs are likewise only bounded by the reported maxima once
the module has run in game.
