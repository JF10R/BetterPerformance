# Self-contained implementation prompt: ValheimPlus map sync correctness and cost

Impact estimate before you start (from the 2026-09-15 two-player session and the decompiled 0.10.1.2 build):

- Performance: each join costs the dedicated server one 53 to 68 ms main-thread stall (`ExplorationDataToMapRanges`, a per-bit scan of 4.2 M pixels), and every connected client one unmeasured main-thread stall (per-pixel `GetPixel`/`SetPixel` apply, estimated tens of ms). Four server executions in 76 minutes. Modest in absolute terms: it is a per-join event, not a per-frame cost.
- Correctness and function: two off-by-one defects erode explored pixels on every round trip, a null dereference on an empty world, a thread-unsafe five-minute save, and no resync during a session (exploration is shared only at spawn). These matter more than the milliseconds.

Verdict: worth a PR series, led by correctness, with the performance changes as a second PR carrying before/after measurements. Land upstream PR #163 (pin sharing) first; it is independent.

---

You are implementing changes in a fork of ValheimPlus (https://github.com/Grantapher/ValheimPlus), a BepInEx 5 / Harmony mod for Valheim 1.0.12, C# targeting .NET Framework 4.7.2. Work on a new branch from the `0.10.1.2` tag (commit `19ff6cb`). Do not touch `VPlusMapPinSync.cs` or anything pin-related; pin sharing is a separate open PR (#163). Keep the wire format (`VPlusMapSync` routed RPC: `int32 rangeCount | rangeCount × {int32 StartingX, int32 EndingX, int32 Y} | bool isLastChunk`, 10,000 ranges per package) backward compatible unless the PR explicitly bumps a protocol version and handles both.

## Files

- `ValheimPlus/RPC/VPlusMapSync.cs`: `RPC_VPlusMapSync` (server merge, full re-encode, broadcast), `ExplorationDataToMapRanges(BitArray)`, `ChunkMapData`, `SendMapToServer`, `LoadMapDataFromDisk`, `SaveMapDataToDisk`.
- `ValheimPlus/GameClasses/MinimapAwake.cs`: allocates `ServerMapData = new BitArray(m_textureSize * m_textureSize)` and starts `MapSyncSaveTimer`.
- `ValheimPlus/GameClasses/PlayerPositionWatcher.cs`: marks a disc explored around each player every 2 s on the server.
- `ValheimPlus/Utility/RpcQueue.cs`, `ValheimPlus/GameClasses/PeriodicDataHandler.cs`: one chunk per `ZNet.SendPeriodicData` (every 2 s), single global ack flag.
- `ValheimPlus/ValheimPlusPlugin.cs`: `MapSyncSaveTimer` (System.Timers.Timer, no SynchronizingObject, fires on a ThreadPool thread).
- `Minimap.Explore(int x, int y)` in the game: reads `m_explored[y * m_textureSize + x]`, then `m_fogTexture.GetPixel`/`SetPixel` zeroing only the red channel, sets the bit. The green channel carries `m_exploredOthers`; never clobber it.

## PR 1: correctness (small, testable, no behavior expansion)

1. Off-by-one at run ends. The encoder stores an inclusive end (`endX = x - 1`) while both decoders loop `for (j = StartingX; j < EndingX; j++)`; the end-of-row branch writes an exclusive end. Make `EndingX` exclusive everywhere (`endX = x`), so both branches agree and a run of length 1 transmits. Preserve compatibility: a receiver must accept packets from an old sender; document the one-pixel difference.
2. Run-flush skips a pixel. The state machine flushes on the iteration after a run ends and `continue`s past that pixel, so `[1,0,1]` loses the third pixel. Make the flushing iteration also able to start a new run.
3. Null guard. `ChunkMapData` returns `null` for an empty list and the server branch iterates it unguarded; guard as `SendMapToServer` already does.
4. Cache `Minimap.instance.m_textureSize` into a plain static int once in `MinimapAwake` and use it everywhere; never read a `UnityEngine.Object` member from the timer thread.
5. Unit tests: a small console test project (mirror the pattern in `tests/MapPinSync/` from PR #163) that round-trips synthetic `BitArray` rows through encode/decode and asserts bit-exact equality for: single pixel, two adjacent runs separated by one gap, full row, empty row, last column.

## PR 2: server cost and traffic

1. Word-level scan: replace the per-bit inner loop of `ExplorationDataToMapRanges` with `BitArray.CopyTo(int[])` into a preallocated `int[size*size/32]` and scan 32 bits at a time, skipping `0` words (unexplored) and handling `-1` words as full runs. Expected: 53 ms → low single-digit ms. Keep the output identical to PR 1's fixed encoder (assert in tests).
2. Dirty flag and cached chunks: set `mapDirty` in the two places that mutate `ServerMapData` (merge loop, `PlayerPositionWatcher`); cache the encoded chunk list and reuse it when not dirty.
3. Send the full map only to the joiner (`rpcData.Target = sender`) and broadcast only the joiner's newly contributed ranges to the other peers.
4. Optional, behind a config key default off: periodic delta resync every N minutes (send only ranges set since the last sync per peer).
5. Measure: log `Stopwatch` elapsed for the scan and the client apply before and after, and put the numbers in the PR description.

## PR 3: client apply and disk format

1. Client apply in bulk: `GetPixels32()` once, mutate `.r = 0` and set `m_explored` bits for every range, `SetPixels32()` once, `Apply()` once. Do not call `Minimap.Explore` per pixel. Verify the texture format (`R8G8_UNorm` with RGBA32 fallback) and keep the green channel intact. If a join still stalls, drain ranges over several frames from a coroutine with one `Apply()` at the end.
2. Disk format: replace the comma-joined index string with `BitArray.CopyTo(byte[])` → `File.WriteAllBytes` (fixed 512 KB for 2048²), with a magic header and version byte; keep a legacy text loader for existing `<world>_mapSync.dat` files.
3. Threading: snapshot `ServerMapData` to a `byte[]` on the main thread (from an existing main-thread hook), then write on a background task; never read `ServerMapData` or any Unity object from the timer thread.

## Do not

- Do not touch `m_fogTexture`, `Minimap.instance`, `ZRoutedRpc` or `ZNet` state off the main thread.
- Do not raise the `RpcQueue` drain rate; it is bounded by `ZNet.SendPeriodicData`'s 2 s timer and a single global ack. If throughput matters, replace the ack with a per-(target, sequence) map first, or bypass the queue with a self-rate-limited sender as PR #163 does for pins.
- Do not change the routed RPC name or registration order.

## Definition of done per PR

Builds against the game assemblies; unit tests pass; a two-client in-game test on a disposable world shows identical explored sets on both clients after join, after one player explores, and after reconnect; PR description carries the before/after timings and the exact commits it is based on.
