# Server and host research, 2026-09-15

Read-only review of decompiled Valheim 1.0.12, ValheimPlus 0.10.1.2 and BetterNetworking 2.3.3,
plus read-only Windows event queries. No game launched, no setting changed.

## F1 — What costs 1.2 ms inside `RPC_Damage`

**Verdict: measure first, with a per-target split. Support recomputation is ruled out; unpooled
`Instantiate` is the prime suspect.**

**Evidence (VERIFIED).** Five classes register the same routed name, so one attribution row
blends them: `Character.cs:705`, `WearNTear.cs:271`, `Destructible.cs:65`, `TreeBase.cs:48`,
`MineRock5.cs:136` — the last with a different signature,
`m_nview.Register<HitData, int>("RPC_Damage", RPC_Damage)`.

- **Damage numbers are unpooled UI.** Every owner-side hit reaches `DamageText.ShowText`.
  `AddInworldText` does `Object.Instantiate(m_worldTextBase, base.transform)` plus a
  `Localization.Localize` per hit, and `UpdateWorldTexts` destroys at most **one** expired entry
  per frame, so under sustained fire the list grows and each survivor costs a
  `WorldToScreenPointScaled` every frame.
- **Hit effects are unpooled prefabs.** `EffectList.Create` calls
  `UnityEngine.Object.Instantiate(effectData.m_prefab, position, rotation)` per entry and may
  `AddComponent<ParentConstraint>()`. `Destructible.cs:136-137` calls `m_hitEffect.Create` twice;
  `WearNTear.cs:1225-1230` creates it again on physical damage.
- **Destroy-time work is inside the same RPC.** `WearNTear.Destroy` runs
  `m_piece.DropResources(hitData)`, which instantiates a container and clones item data per stack
  (`Piece.cs:379`), then `m_destroyedEffect.Create` and `ZNetScene.instance.Destroy`.
  `MineRock5.DamageArea` instantiates every `m_dropItems.GetDropList()` entry. Plausible 9.4 ms
  tail, not the mean.
- **`MineRock5.DamageArea` logs unconditionally**: `ZLog.Log("hit mine rock " + hitAreaIndex)`
  before any work.
- **Support cascade is NOT here.** `WearNTear.RPC_Damage` never calls `UpdateSupport`; only
  `Destroy` calls `ClearCachedSupport`, three `List.Clear()` calls (`WearNTear.cs:808`).
  `UpdateSupport` is driven from the wear update at `WearNTear.cs:568`.

**Candidate that preserves combat outcomes exactly.** Pool the `DamageText` world-text objects
and expire all due entries per frame instead of one. Client visuals only: no health, no ZDO, no
RPC.

**Probe to run first.** Split the `routed_rpc` key for `RPC_Damage` only, by the target ZDO's
prefab hash, resolved at export through the existing `ZNetScene.GetPrefab(int)` cache from
`attribution-telemetry.md`. One hash read inside the hook. Restrict the split to an RPC allow-list
so prefab variety cannot push other rows past the 256-key cap into `other`.

**What not to do.** Do not remove the duplicated `m_hitEffect.Create` calls — that halves visible
particles. Do not batch, defer or coalesce damage RPCs; ordering and the owner-only guards decide
combat outcomes.

## F2 — Peer disconnect stalls the server 105 ms

**Verdict: real, fully explained, not worth acting on for a two-player server. Add the counter,
not the fix.**

**Evidence (VERIFIED).** `ZNet.Disconnect` calls `ClearPlayerData`: `m_routedRpc.RemovePeer` is a
single `List.Remove` (`ZRoutedRpc.cs:80-83`), and the whole cost is one line inside
`m_zdoMan.RemovePeer` — `if (ZNet.instance.IsServer()) { RemoveOrphanNonPersistentZDOS(); }`
(`ZDOMan.cs:834`).

`RemoveOrphanNonPersistentZDOS` (`ZDOMan.cs:1458`) iterates **all** of `m_objectsByID`, a
`Dictionary<ZDOID, ZDO>` holding the entire loaded world, not a sector. Per entry the test is
cheap — `Persistent` is a bitflag on `m_dataFlags`, `HasOwner()` returns `Owned` — so the cost is
the traversal over pointer-chased heap objects. Each orphan destroyed also emits
`ZLog.Log("Destroying abandoned non persistent zdo " + uid ...)`. The 1→0 peer case cost the same
105 ms, which is what a count-independent full scan predicts.

`ConvertOwnerships` is **not** on this path: it runs only from `ZDOMan.Load` under
`if (version < Version.World.NewSaveFormat)` (`ZDOMan.cs:675`), a save-format migration.

**Prior art (REPORTED).** [ValheimCommunityPatch](https://github.com/MidnightsFX/Valheim-Community-Patch)
ships `Fix Disconnect ZDO Sweep`, indexing non-persistent ZDOs by owner so a disconnect touches
only the leaving player's objects. BetterNetworking 2.3.3 does not go near this — its patches are
compression, send rate, update rate, queue size and player limit only.

**Measured opinion.** Three events in 76 minutes at 105 ms is 0.007 % of wall time on a server
whose main thread sits at 6.7 % CPU. Spreading the sweep leaves a window in which orphan ZDOs are
still owned by a gone peer, the exact state the sweep exists to clear. Real risk against an
invisible gain. Skip it.

**Probe.** Add `zdoman_objects_by_id` beside `zdoman_dead_zdos` in
`src/BetterPerformance/OwnershipTelemetry.cs:158`, same reflected-field pattern. It turns the
105 ms into a per-ZDO rate.

## F3 — ValheimPlus map sync

**Verdict: exploration sync works and is what costs 53-68 ms. Pin sharing is dead code in the
installed build. The user is right, and the upstream PR already exists.**

**Exploration sync is active (VERIFIED).** `MinimapAwake.Postfix` allocates
`VPlusMapSync.ServerMapData = new BitArray(m_textureSize * m_textureSize)` when the server has
`[Map] enabled` and `shareMapProgression`. `Player_OnSpawned_Patch` calls
`VPlusMapSync.SendMapToServer()` on every spawn, matching the four server-side executions.

**Where the 53-68 ms goes (VERIFIED).** `ExplorationDataToMapRanges` walks the full square in a
nested loop over `Minimap.instance.m_textureSize`, indexing the `BitArray` once per pixel, and
re-dereferences `Minimap.instance.m_textureSize` three times per iteration. At 2048 that is
4,194,304 indexer calls plus branches — 12.7 ns each, the right order for a `BitArray` indexer —
and the server runs the whole scan again after merging each client's ranges. The session's own
`MapSerialization` at 17-65 ms over the same 2048² `BitArray` independently calibrates it.
`m_textureSize` is 256 in the decompiled field initializer but is a serialized prefab field; 2048
is REPORTED, corroborated by `frontier-map-benchmark.md` and the 6.3 s `WorldMapGenerate`. Read it
at runtime before quoting a number upstream.

The scan got *slower* in this very build: Grantapher's `df003d4`, "Fix map sync against BitArray
exploration data", is an ancestor of `19ff6cb` and moved the hot loop from array indexing to a
`BitArray` indexer, cutting memory 8× and raising per-element cost.

The network payload **is** already chunked, 10,000 ranges per `ZPackage` (`ChunkMapData`), but
`RpcQueue` drains one chunk per `ZNet.SendPeriodicData` tick and that tick is gated by
`m_periodicSendTimer >= 2f` (`ZNet.cs:1557`). The CPU scan is not chunked at all.

**Two further design gaps (VERIFIED).** The server broadcasts the whole map to **everyone** on
every join — `rpcData.Target = 0L` — not a delta to the joiner, so each join also costs every
other client a full apply. And sync fires exactly once per client session, from
`Player_OnSpawned_Patch` behind `ShouldSyncOnSpawn`, with no periodic resync: two players
exploring for hours never see each other's new ground until someone reconnects.

**Three defects worth reporting upstream (VERIFIED by reading).** `EndingX` is written as the
**inclusive** last explored index (`num2 = j - 1`) while both receivers loop **exclusively**
(`for (j = StartingX; j < EndingX; j++)`), so every run loses its last pixel and a run of length 1
transmits nothing; the end-of-row branch meanwhile writes an exclusive `EndingX = m_textureSize`,
so the two branches disagree. Separately, the `else if (num > -1 && num2 > -1)` flush branch
consumes a pixel without testing it, so the column right after a run end can never start the next
run. And `ChunkMapData` returns `null` for an empty list while the server branch iterates it
unguarded — a `NullReferenceException` on a brand-new world.

**Client-side cost (VERIFIED).** The receive path calls `Minimap.Explore(l, y)` per pixel, and
`Minimap.cs:1857-1868` does a `m_fogTexture.GetPixel` plus `SetPixel` each time — two managed-to-
native Unity calls per pixel, hundreds of thousands per sync, in one frame.

**Pin sharing is dead code (VERIFIED).** `MapPinEditor_Patches.Minimap_AddPin_Patch` gates on
`shareablePins.Contains(__result.m_type)`, and `shareablePins` is declared
`new List<Minimap.PinType>()` and **never populated anywhere in the installed assembly** — grep
returns only the declaration and the `Contains` call. It was filled by the retired custom pin
editor, whose leftover fields (`pinEditorPanel`, `mapPinBundle`, `sharePin`) are also unreferenced.
So `shareAllPins=true` silently does nothing. Long-standing user reports match:
[#602](https://github.com/valheimPlus/ValheimPlus/issues/602),
[#672](https://github.com/valheimPlus/ValheimPlus/issues/672),
[#732](https://github.com/valheimPlus/ValheimPlus/issues/732).

**The user's branch.** `codex/restore-share-all-pins` (`8eefea0`, `4f912c5` on `19ff6cb`) rewrites
`VPlusMapPinSync` into a batched, validated protocol: an explicit `IsShareableType` allow-list
replacing the dead list, a protocol version, a 50-pin package cap, a 128 KB size cap,
finite-position and name-length validation, a 0.2 s send interval, and a join-time snapshot via a
new `VPlusRequestMapPins` RPC. It also bypasses `RpcQueue` deliberately, which is right given that
queue's 2 s drain and single global `_ack` flag. It removes 254 lines of dead editor from
`Minimap.cs` and adds an offline test project under `tests/MapPinSync/`. Upstream it is
[PR #163](https://github.com/Grantapher/ValheimPlus/pull/163), **draft**, competing directly with
[PR #162](https://github.com/Grantapher/ValheimPlus/pull/162), which proposes deleting the option
instead. The bug it fixes is [issue #57](https://github.com/Grantapher/ValheimPlus/issues/57), open
since 2024-02-24. The author's own note says it is not yet tested in-game, which is exactly what
#162 has to be beaten on.

**Suggestions, in sequence.**

1. **Land #163 first, unchanged.** Its blocker is a two-client in-game validation, not code. Pins
   are hundreds of items and will never show up beside the exploration scan. Do not fold a perf
   rewrite into it — that hands the maintainer a second reason to prefer #162.
2. **Correctness PR:** the three defects above, plus caching `m_textureSize` into a plain
   `static int` at `MinimapAwake` instead of dereferencing a Unity object per iteration. Small,
   testable, uncontroversial, and it gives the perf work a correct baseline.
3. **Perf PR:** scan words, not bits. `BitArray.CopyTo(int[])` then skip whole zero words — an
   unexplored ocean is almost all zeros — turns 4.19 M indexer calls into 131,072 int reads. Add a
   dirty flag at the two `ServerMapData` write sites so a join that contributed nothing skips the
   scan entirely, and change `Target = 0L` to the joining peer, broadcasting only the delta to the
   others. Lead with the measured 53.5 / 53.5 / 67.9 ms: no upstream issue reports this stall.
4. **Client PR:** replace the per-pixel `Explore` loop with one `GetPixels32`, a bulk mutate, one
   `SetPixels32`, one `Apply` — and set `m_explored` yourself, since that bypasses `Explore`. Check
   the channels first: `Explore` zeroes only `.r`, and green carries `m_exploredOthers`.
5. **Disk and threading PR:** `BitArray.CopyTo(byte[])` to a fixed 512 KB file behind a magic header
   with a legacy-text fallback, replacing the comma-joined multi-megabyte string. Fix the race by
   snapshotting on the main thread and writing on the worker.

**What not to do.** Do not touch `m_fogTexture` off the main thread — `GetPixel`, `SetPixel`,
`GetPixels32`, `SetPixels32` and `Apply` are all main-thread-only. Do not read
`Minimap.instance.m_textureSize` or iterate `ServerMapData` from a worker; the installed code
already does both from the 5-minute save timer, and that is a latent bug, not a precedent — the
timer has no `SynchronizingObject`, so it races the main thread's writes to a non-thread-safe
`BitArray`. Do not raise the `RpcQueue` drain rate: `_ack` is one global bool with no per-peer
tracking, so faster pumping interleaves acks across peers and desyncs the chunk stream.

## F4 — The 1.66 s machine-wide freeze at 19:43:29 local

**Verdict: Windows Update. Cumulative security update KB5129195 committed its component-store
transaction at exactly 19:43:29. High confidence.**

**Evidence (VERIFIED, read-only `Get-WinEvent`).**

| local time | log | id | event |
| --- | --- | ---: | --- |
| 19:39:11 | Setup | 2 | KB5129195 changed to Staged |
| 19:39:19 | Setup | 1 | Initiating changes for KB5129195, target Installed, client `UpdateAgentLCU` |
| 19:43:27 | CBS log | — | `CbsTransactionClose`, then `CbsTransactionCommit` |
| **19:43:29** | **CBS log** | — | `FinalCommitPackagesState: Completed persisting state of packages`; `Exec: End: nested restore point - complete.`; `CbsTemp` delete and rename |
| **19:43:29.717** | **Setup** | **4** | **"A reboot is necessary before package KB5129195 can be changed to the Installed state"** |
| 19:43:34.805 | System | 4 | Virtual Disk Service stopped |
| 19:44:17 | System | 7040 | BITS set to auto start |

`Get-HotFix` lists KB5129195, Security Update, 2026-09-15. The CBS recap gives
`Total time: 227316 ms`, `Planning: 187579 ms`, `Installing/Uninstalling: 39655 ms`, so
TrustedInstaller ran continuously from 19:36:20 — the games were probably hitching for those seven
minutes, and 19:43:29 is the worst one because it is the commit.

The commit bundles a transacted NTFS commit over WinSxS, a registry hive flush, the close of the
update's own nested restore point, and a temp-directory delete and rename. Those are machine-wide
serialization points: other processes block on the same locks rather than burning CPU, which is
what 5-38 % main-thread CPU with no game method on the stack looks like.

**Alternatives rejected.** The Virtual Disk Service stop is 5 s *after* the freeze, so it is a
consequence of servicing ending, not the cause; the session report's framing should be corrected.
Store, Kernel-PnP, DeviceSetupManager and Defender operational logs returned **no events** in the
window. No Kernel-Power, no Kernel-EventTracing, no NTFS event at 19:43. A GPU TDR always logs
System 4101 and none appears — and a TDR could not freeze the headless server anyway. Steam lives
on `D:` and no Steam log was written between 19:30 and 20:00. Windows Update Client logged only
scans, at 19:31. A sweep of all 119 logs holding records found nothing else in 19:42:30-19:44:30.

Residual uncertainty: Windows never logs "a lock was held for 1.66 s", so the stall mechanism is
inferred from the operations in flight. Proving it outright needs an ETW trace with stack walking
on registry and file I/O during a repeat.

**Mitigations (user's call, nothing was changed).**

- **Pause updates before a session.** Directly addresses this incident: the install was staged and
  chose its own moment. Strongest of the four.
- **Set Active Hours to cover evening play.** Same mechanism, less blunt; defers the install rather
  than the download.
- **Reboot promptly after an update stages.** The session ended `Reboot required: yes` and the
  scheduler re-armed `MusNotification` and `Schedule Work` seconds later. That reboot is most
  likely **still pending on this machine**, so the same commit path can re-arm.
- **Store auto-update off, Defender exclusions, Game Mode, moving the game off `C:`** —
  speculative or already true. Those logs were empty, the game is already on `D:`, and Game Mode
  adjusts scheduling priority without exempting a process from lock-based commits.
- **Disabling System Restore is not the fix.** The restore point was CBS's own nested one inside
  the update transaction, not a scheduled task, so disabling those tasks changes nothing — and it
  removes the rollback net exactly while updates install.

## Observed, not fixed

- `Destructible.cs:136-137` creates the same hit effect twice, once with a gamepad ZDOID and once
  without. Possibly intentional layering, possibly a duplicate.
- `MineRock5.DamageArea` logs at info level on every mining hit.
- ValheimPlus `SaveMapDataToDisk` serializes explored pixels as comma-separated text and runs a
  full 2048² double loop on a 5-minute `System.Timers.Timer` with no `SynchronizingObject`, so it
  reads Unity object state and a non-thread-safe `BitArray` from a thread pool thread while the
  main thread writes them.
- ValheimPlus still ships an 895 KB `map-pin-ui` asset bundle that nothing loads.
- `Microsoft-Windows-WinREAgent` event 4502, "Windows Recovery Environment servicing failed", at
  19:36:18 local, just before KB5129195 began. Unrelated to the freeze, but WinRE did not get
  patched alongside it.
