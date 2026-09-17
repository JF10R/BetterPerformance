# Speculative map pre-compression — research, 2026-09-17

Read-only research. No game process was started, no source was modified.
Question: can the 60–87 ms of native gzip inside `Game.SavePlayerProfile` be moved off the
synchronous save by compressing speculatively on a worker thread and priming the existing exact
cache, with byte-identical output?

**Verdict: feasible, and correctness is free; only the hit rate is at risk.** Expected saving
about **50 ms per primed save** (305 ms native compression over 6 misses = 50.8 ms each), i.e.
`CharacterSave` 123–174 ms drops to roughly 73–124 ms when the speculative entry is still valid.
A stale entry costs nothing but the worker's CPU: the exact byte compare fails and the native
path runs, producing exactly what it produces today.

## 1. The save path (verified by reading)

| Step | Where | Thread |
|---|---|---|
| `SavePlayerProfile` calls `Minimap.SaveMapData()` | `Game.cs:452`, inside the mount/`try` block | main |
| `SaveMapData` → `GetMapData()` → `PlayerProfile.SetMapData` | `Minimap.cs:2118`, `2123` | main |
| inner package build: `m_textureSize`, then one byte per bit of `m_explored`, then one per bit of `m_exploredOthers`, then saved-pin count and pins, then `ZNet.instance.IsReferencePositionPublic()` | `Minimap.cs:2124-2151` | main |
| `zPackage.WriteCompressed(zPackage2)` → `Utils.Compress` → `GZipStream` | `Minimap.cs:2153` | main |

`SaveMapData` has exactly **one** caller repo-wide (`Game.cs:452`), and `GetMapData` exactly one
(`SaveMapData`). The whole map cost is inside the synchronous profile save.

Input bytes: `m_explored` and `m_exploredOthers` are `BitArray` (confirmed by
`FastMapSerialization`'s field contract and by `ResetAndExplore`'s `SetAll`), serialized one byte
per cell. At `m_textureSize` 2048 that is 4 + 2×4,194,304 + 4 + pins + 1 = the observed
8,388,617 bytes. The **live state is only ~1 MB** (two `BitArray` backing `int[]`); the 8.4 MB is
expansion, not state.

What changes while exploring: `Explore(int,int)` sets one bit at a time from `UpdateExplore` on
the main thread; `ExploreOthers` does the same for shared map data; `ResetAndExplore` and
`SetMapData` rewrite both wholesale on load. Pins and the reference-position flag change rarely.
This matches the session: 6 misses in 7 lookups while exploring.

## 2. Reading the input off the main thread

**Do not.** `m_explored` is mutated by `Explore` from `Minimap.Update` on the main thread with no
synchronization, so a worker reading it directly would tear. Nothing in the payload requires a
Unity API, though: `BitArray`, `PinData` (`string`, `Vector3`, `PinType`, `long`,
`PlatformUserID`) and a `bool` are all plain managed data, and `ZPackage` is a `MemoryStream` +
`BinaryWriter`. So take a **main-thread snapshot** and hand plain data to the worker:

- `BitArray.CopyTo(int[])` twice — two 512 KB block copies, on the order of 0.1–0.3 ms total.
- the saved pins flattened into a plain struct list (typically tens of entries).
- `m_textureSize` and `IsReferencePositionPublic()` read once.

Everything after that is thread-safe by construction.

## 3. Design

**Serialize on the worker with the game's own writer.** The worker builds a real `ZPackage` and
issues the same `Write` calls in the same order as `GetMapData`, only sourcing bits from the
snapshot. Byte-identity of the *encoding* is by construction (same `BinaryWriter` methods, same
order); byte-identity of the *result* is then enforced anyway by `ExactByteCache.TryWrite`, which
compares all 8.4 MB before it will emit the cached output. **Correctness does not depend on the
worker being right.** A wrong or stale snapshot is a miss.

Hook points:

- **Dirty counter**: postfix `Minimap.Explore(int,int)` and `Minimap.ExploreOthers(int,int)`,
  increment when they return `true`; also bump on `SetMapData`/`ResetAndExplore` and on any pin
  mutation. This is the cheap validity predicate, not the correctness guarantee.
- **Snapshot + dispatch**: from the existing main-thread update pump, when the counter has been
  stable for N ms (start with 250 ms) and a save is plausibly near.
- **Publish**: the worker parks `(input[], compressedOutput[])`; the main thread adopts them into
  the cache on its next pump. Publishing on the main thread means **no change to
  `ExactByteCache`'s thread model** and no lock on the save path. Adopt by reference (a new
  `Adopt` entry point beside `Store`) so publication costs no copy; `Store`'s current
  `Buffer.BlockCopy` of 8.4 MB would otherwise cost 1–2 ms on the main thread.
- Stamp each snapshot with the `ZNet.instance` generation `MapCompressionCache` already tracks;
  refuse to publish a snapshot from a previous world.

Cache-key equality: the cache key *is* the complete inner-package bytes, so a primed entry is
indistinguishable from one stored by a real save. `Write` keeps its existing guards (main thread,
`busy`, `Compatible()`, stream/writer shape) unchanged.

Memory: snapshot ~1 MB + worker staging 8.4 MB + cached input 8.4 MB + compressed output. The
session reported `map_cache_retained_bytes` 8.4 MB for input plus output together, so the gzip
output is small (the bit arrays are mostly zero). Peak about **18 MB**, against the cache's
existing 24 MiB bound. Reuse one worker staging buffer to keep these off the LOH churn path.

CPU per speculative run: about 50 ms of gzip plus the serialization loop, on one background
thread. Budget it — do not recompress on every explored pixel. Trigger on stability plus
proximity to an expected save, and cap runs per minute.

Map changed after the snapshot: `TryWrite` compares and fails, `misses++`, `WriteCompressed`
runs, the result is the vanilla bytes, and the fresh pair replaces the stale one in the cache.
Identical output, no fallback path, no behaviour change.

## 4. Alternatives

- **Compress on a game-owned save worker.** There is none for the profile: `SavePlayerProfile`
  and `PlayerProfile.Save` are fully synchronous. The world-save thread exists but is a different
  lifecycle and shares unsynchronized statics (`SaveSystem` collections, the mount refcount), the
  hazard already recorded in the character-save research. Rejected.
- **Shrink the input** by writing `m_explored` packed (8× smaller) or dropping
  `m_exploredOthers`/pins. `SetMapData` reads exactly `m_explored.Length` bytes at map version 8;
  changing that changes the on-disk format, so vanilla and every other mod would misread the
  character. Rejected outright.
- **A faster deflate at the same call site.** Any other implementation yields different bytes.
  `SetMapData` would still decompress it, but it breaks the byte-identical guarantee this plugin
  holds itself to and would invalidate the existing parity fixture. Out of scope.

## 5. Telemetry to prove the hit rate

Beside the existing `map_cache_*` gauges: `map_precompress_runs_total`,
`map_precompress_snapshot_ms_total`, `map_precompress_worker_ms_total`,
`map_precompress_published_total`, `map_precompress_superseded_total` (published then invalidated
before any save), `map_cache_hits_primed_total` (hits served by an entry no save produced) and
`map_precompress_dirty_delta_at_save` (dirty counter at save minus at snapshot). The last one is
the diagnostic that says *why* the hit rate is what it is, independently of the byte compare.

## 6. Risks

Thread safety is the main one and is handled by snapshotting on the main thread and publishing on
the main thread; no Unity API is touched from the worker. Memory grows by about 18 MB peak. The
worker burns ~50 ms of one core per run, so an unbounded trigger is the realistic way to make
this a net loss on a weak machine. Cloud write ordering is untouched: nothing moves off the save
path, only the cache is warmed ahead of it. Worst case is exactly today's behaviour.

## Split: verified vs assumed

**Verified by reading**: the call chain and its single caller; the inner-package field order; the
`BitArray` types and their main-thread writers; the compression call site; `ExactByteCache`'s
full-compare semantics and its copy-on-store; `MapCompressionCache`'s main-thread and contract
guards; that no Unity API is required to re-encode the payload.

**Assumed, not measured**: the 0.1–0.3 ms snapshot cost; the ~50 ms worker compression (inferred
from 305 ms over 6 misses in the 2026-09-16 session); the hit rate any trigger policy achieves;
the gzip output size; that a `ZPackage` built off-thread reproduces the native bytes — enforced,
not assumed, by the byte compare, but the *hit rate* depends on it and only a Unity run shows it.

Implemented: [Speculative map pre-compression](map-precompression.md).
