# Save optimization research — 2026-09-15

Analysis only. No third-party save mod was installed, no save path was replaced, and no game process was started. Repository snapshots were read from isolated ignored research directories.

### Existing implementations

**AsyncSave** is directly relevant to client stalls. In inspected commit `07b90b229bc0d933bbad68511e4fb81280c09e55`, `MapSnapshot.Capture` captures pins and copies both exploration arrays into a reusable payload with `Buffer.BlockCopy`. `Compress` uses GZip with `CompressionLevel.Fastest` and builds the vanilla-shaped map package. This avoids per-boolean writes and some large intermediate copies. This is an implementation observation, not a measured speedup in BetterPerformance. [MapSnapshot source](https://github.com/MidnightsFX/Valheim_AsyncSave/blob/07b90b229bc0d933bbad68511e4fb81280c09e55/AsyncSave/Profile/MapSnapshot.cs).

The character pipeline keeps live state reads and package construction on the main thread, runs compression/file work on workers, returns completion notification to the main thread, and drains pending work during shutdown. Its writer performs hashing, temporary-file writing, replacement, cloud-failure fallback and backup consideration. These details matter: changing only the outer method to run asynchronously would omit necessary coordination. Source inspection is not a full safety or compatibility audit. [Profile pipeline](https://github.com/MidnightsFX/Valheim_AsyncSave/blob/07b90b229bc0d933bbad68511e4fb81280c09e55/AsyncSave/Profile/ProfileSave.cs), [file writer](https://github.com/MidnightsFX/Valheim_AsyncSave/blob/07b90b229bc0d933bbad68511e4fb81280c09e55/AsyncSave/Patches/SavePlayerToDiskPatch.cs).

**SmoothSave** mainly addresses world-save preparation. Inspected commit `9050fe20974a1efaea384f047214881f9f3fd86a` spreads ZDO copying across coroutine steps and tracks changes/removals/additions while copying. It handles synchronous save/stop paths separately. This is not a direct replacement for client character-save optimization. [SmoothSave source](https://github.com/blaxxun-boop/SmoothSave/blob/9050fe20974a1efaea384f047214881f9f3fd86a/SmoothSave/SmoothSave.cs).

### Implications for BetterPerformance

**Material compatibility finding:** inspection of the installed Valheim 1.0.12 client assembly shows `Minimap.m_explored` and `m_exploredOthers` are `System.Collections.BitArray`, and `GetMapData` reads them with `BitArray.get_Item`. The inspected AsyncSave snapshot instead passes these fields directly to `Buffer.BlockCopy` as if they were primitive arrays. Its implementation cannot be transferred unchanged to this installed layout. This finding concerns the inspected source commit; the published 0.5.0 binary was not loaded or proven identical to it.

The relevant adaptation is expanding packed bits into vanilla's one-byte-per-boolean output in bulk, using exact output comparison. The synthetic benchmark must use the installed BitArray representation as its primary baseline; a bool-array benchmark alone would test an older representation and could mislead the decision.

The most contained candidate is bulk minimap serialization with fewer allocations/copies while retaining the existing save lifecycle. Byte-for-byte payload tests, version-specific field verification, and reload tests on disposable saves would be necessary before using it. Moving compression/hash/write to an ordered worker is a separate, broader candidate; completion, errors, cloud/local behavior, logout and overlapping requests must remain coherent.

Our new measurements separate map serialization, character save/file work, world snapshot copying and selected RPC/replication stages. They can show which phase merits intervention. They do not yet isolate compression from other map work, pure disk latency, exclusive CPU time, or total action latency.

Potential independent work includes eliminating redundant copies, reusable bounded buffers, and measuring main-thread blocking separately from end-to-end save completion. A map cache would need complete invalidation for exploration, shared exploration, pins and mod changes; no such cache is implemented. No source was imported from either mod, and no measured runtime improvement or current mod-combination compatibility is claimed.
