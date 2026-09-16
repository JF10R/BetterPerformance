# Data-safety audit for 0.4.1

Source review, 2026-09-15. Covers FastMapSerialization, MapCompressionCache,
PackageCopyOptimization, ObjectCreationBudget and their Core helpers/tests.
The installed client configuration enables bulk map serialization, exact map
compression caching, local package copying and the creation budget with deferred
preparation. Loot priority remains disabled. This review cannot guarantee that
any mod combination or every failure condition is free of data loss.

### Findings and changes

| Finding | Consequence | Resolution in source |
| --- | --- | --- |
| Bulk map serialization accepted a standard BinaryWriter over a custom stream | A stream can transform bulk writes differently from native individual boolean writes, changing exploration bytes | Require exact MemoryStream type; new regression checks fallback before output and demonstrates chunk-sensitive behavior |
| Package copy checked serialization/write patches but omitted constructor and Clear patches from its ownership fence | Such patches could retain/expose the otherwise local source package, invalidating exclusive buffer ownership | Any Harmony owner on parameterless constructor or Clear now forces native copy for the send scope; regressions cover both boundaries and nested scope restoration |

These were conditional compatibility gaps, not observed corruption in the
installed mod set. The new regressions were written but not executed by this
review stream; the integrating build/test result is the verification authority.

### What the implementation protects

- **Map exploration/pins:** bulk serialization preserves the native one-byte-per-
  boolean encoding and limits its replacement to two verified loops. Pins,
  version fields, compression and native save lifecycle remain outside that
  replacement. Oversized or unsupported inputs fall back before output.
- **Compressed map cache:** hits require equality of every serialized input byte,
  not only a hash or guessed dirty flag. Input and encoded output are owned copies.
  Pin/exploration changes participate in equality. Stream identity, shared backing
  arrays, custom writers/streams, thread, recursion and late compressor/GetArray
  patches are guarded. Cache-store allocation failure drops the cache entry after
  native compression; it does not discard the successful native output.
- **Network package copying:** only one verified local SendZDOs copy site changes.
  Length prefix and complete source contents are preserved. Hidden/aliased/custom
  buffers use native snapshot behavior. The direct buffer is copied synchronously
  into the destination; it is not handed to the transport for later reuse.
- **Exceptions:** accepted bulk/cache/package writes propagate failures. They do
  not retry native writes after a partial write, which could append duplicate
  bytes and corrupt the package. Native save success/failure handling remains
  responsible for persistence; these optimizations do not make saving atomic.
- **Objects:** the budget changes when native creation is attempted. It does not
  remove ZDOs to reduce workload, force readiness, assign ownership or claim an
  uncreated object is created. The native server invalid-prefab cleanup still
  exists and is neither suppressed nor newly invented by the budget.

### Remaining risks and validation limits

The creation budget has a real **delayed/missing local appearance** risk even
without disk corruption. Near work can consume the allowance and postpone distant
or later candidates repeatedly; a success floor is not a per-object fairness bound.
Objects can appear late, remain non-interactable, or never appear locally during
a sufficiently changing backlog. The underlying network object is retained, but
longitudinal gameplay consequences and all ownership transitions were not proven
equivalent. The preparation option offers more service time; it does not establish
bounded loading latency or remove expensive individual creation spikes.

The map cache retains at most 24 MiB of owned input/output arrays, with additional
temporary/native allocations during a miss. The bit-writer workspace also retains
buffers per calling thread. Bounds limit accumulation, not total process memory
or peak allocation, and cannot prevent general out-of-memory failures.

Harmony guards inspect selected known contracts, not every possible runtime
detour or arbitrary mod side effect. A mod concurrently mutating map bits or
retaining a package through another unguarded indirect call is outside the proven
ownership assumptions. No blanket compatibility guarantee should be claimed.

Existing isolated run `20260915T115737Z-85e56d` records map parity across explored/
shared bits and added/edited/removed pins; a temporary character save/reload with
matching map; package-copy equality for an owned 4096-byte source; and unchanged
168 protected saves/preferences. Core tests cover chunk/bit boundaries, retained
buffer contamination, reentrancy, exact-cache mutations, aliasing and partial
write errors. Actual installed client/server IL checks reject unsupported layouts.
These are meaningful evidence, but not proof against power loss, full disks,
cloud-sync conflicts, every mod combination, long sessions, or all gameplay
ownership/drop-lifetime cases. No destructive fault-injection test was performed.

The safety conclusion is therefore specific: no direct current-game data-loss
path was identified in the reviewed normal paths; two compatibility gaps were
hardened. Existing save handling and experimental scheduling still carry limits.
