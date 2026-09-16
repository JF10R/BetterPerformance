# Local ZDO package copy

This optional optimization removes the intermediate `byte[]` snapshot from the single `ZPackage.Write(ZPackage)` call inside the installed `ZDOMan.SendZDOs` implementation. It preserves the four-byte length prefix, payload bytes, source position, destination position, flush behavior and synchronous ordering. It does not change networking limits, packet formats, object ownership, delivery ordering or compression.

It is disabled by default. Installation and live enabling are separate so an isolated runtime test can exercise the same binary with the gate on and off. General `ZRpc.Serialize` and routed RPC package copies remain native: their source lifetime is less constrained than the two packages allocated locally inside `SendZDOs`.

## Evidence and supported scope

The current installed client/server package contract is checked against actual IL before installation:

- `ZPackage.Write(ZPackage)` calls the source `GetArray`, writes its length as an `Int32`, then writes the byte array.
- `GetArray` flushes the source `BinaryWriter`, flushes its `MemoryStream`, then calls `MemoryStream.ToArray`. It copies the complete logical contents independently of the current read position.
- Native package constructors allocate their own expandable `MemoryStream`; even the byte-array constructors copy their input into that owned stream.
- In `SendZDOs`, the destination is freshly constructed in local 2; a second fresh package in local 5 is repeatedly cleared, populated by `ZDO.Serialize`, and immediately copied into the destination. The optimized call is at native IL offset `019A` in the inspected build.

Local inspection evidence is retained privately in `.qa/package-copy-callers-il.txt`. Game assemblies and decompiled implementation are not distributed.

The transpiler requires exactly one matching copy call, the two verified local constructors, the exact local operands and no other source-local uses. Only that call instruction changes; all other instructions, labels, exception regions, scheduling and native serialization remain present.

This is a synchronous, exclusively owned package operation, not a general thread-safe replacement for `GetArray`. The implementation never retains a source buffer after the call. Concurrent mutation of a source package, or another mod retaining the local serialization package for asynchronous use, is outside the supported contract. Known Harmony patches to `Write(ZPackage)`, `GetArray`, or `ZDO.Serialize` force native behavior at the beginning of each send operation, including patches installed after this module. Arbitrary runtime method replacement during an active send cannot be made safe by this scope check.

## Fallbacks and failures

The helper falls back before writing when streams/writers are missing, custom subclasses, closed, not associated with the expected stream, not exposing their buffers, or sharing the same underlying array. This includes self-copy, which requires native snapshot semantics. Array-segment origins are honored; an exposed buffer's capacity is never serialized as payload.

Once the length prefix is written, any write error propagates. There is no retry or fallback after a partial write. Native allocation failures can differ because avoiding that allocation is the purpose of this optimization.

The helper allocates no payload buffer and retains none. The destination still performs its normal capacity growth and its final socket serialization can still allocate/copy. Thus avoiding this particular snapshot does not remove all packet allocations or predict FPS or latency gains.

## Metrics and interpretation

`PackageCopyOptimization.Sample(gauges, labels)` emits process-lifetime cumulative counters. Subtract endpoints within one process; never sum repeated cumulative samples. Counters are individually atomic, not one transaction across all fields.

| Counter suffix after `package_local_copy_` | Meaning |
|---|---|
| `calls_total` | Attempts at this specific local copy site |
| `observed_bytes_total` | Readable source logical lengths observed before attempts, including fallback/failed attempts |
| `fast_calls_total` | Successful writes through the optimized helper |
| `avoided_payload_bytes_total` | Sum of successfully copied logical payload lengths; estimated intermediate array payload allocation avoided, excluding headers/alignment |
| `fallback_calls_total` | Calls delegated to the original package writer, including disabled/conflicting/unsupported cases |
| `failed_calls_total` | Exceptions escaping either the fast or native path; overlaps attempted/fallback counts |
| `conflict_scopes_total` | Send operations that used native writes because a relevant method was patched |

Zero-length writes count as calls but contribute zero avoided payload bytes. These counters contain no package contents, peer IDs, world names or character data. Status labels describe installation/conflict state and the current enable gate. The last conflict status may persist after a conflicting patch is removed; counters are the quantitative record.

## Validation and remaining work

Core tests compare native-style serialization with the helper for empty, small, 64 KiB and 1 MiB payloads; arbitrary source position; overwritten destinations; nonzero array origins; self-copy/shared arrays; nonexposed buffers; closed streams; custom writers; and fixed-capacity partial-write errors. Actual game-reference tests verify the original IL, rejection of changed layouts, native package bytes/positions with the gate enabled/disabled, self-copy, and late `GetArray` modifications.

Live performance is unmeasured. Harmony conflict checks occur once per send operation, and atomically updated counters add overhead per copy; for small messages this overhead may outweigh the saved allocation. A useful next measurement compares fixed-binary paired runs under identical object replication load, reporting send duration, allocation/GC, bytes avoided and peer queue behavior. No improvement percentage is asserted from static inspection or parity tests.

Root integration API: `Install(logger)`, writable `Enabled`, `Installed`, `Status`, `Sample(gauges, labels)`, and `Uninstall()`. The optimization should remain opt-in until runtime parity and workload measurements pass.
