# Fish, bird and animal replication: research 2026-09-15

Research only. No game launched, no code changed. Decompiled Valheim 1.0.12 (ILSpy) read out-of-tree, nothing copied. VERIFIED = read in source or computed from the supplied capture. REPORTED = third-party claim, not reproduced here.

## Verdict

VERIFIED: Valheim has **no resend interval and no position threshold**. Any owned object that moved since the last frame is resent on the next 20 Hz peer cycle, and every send re-serializes the **entire** ZDO, never a delta. Fish and birds do nothing special; they are simply always moving, therefore always dirty.

The cheapest correct lever is a **per-prefab minimum resend interval on the send path**, not a simulation change. Because `ZDO.Serialize` always writes current full state, deferring a send loses nothing: the next send carries everything the skipped one would have. Wire format, ownership, persistence and save contents are untouched.

## Mechanism (VERIFIED)

**Dirtying.** `ZSyncTransform.OwnerSync` runs from `CustomLateUpdate`, once per rendered frame.
The gate is exact float equality, not a threshold: `if (!m_positionCached.Equals(position2)) { zDO.SetPosition(position2); }`, then `zDO.Set(ZDOVars.s_velHash, velocity)` in the same shape. `ZDO.InternalSetPosition` calls `IncreaseDataRevision()`, which does `DataRevision++` and, on a client, `ZDOMan.instance.ClientChanged(m_uid)`. A sub-millimetre move dirties the whole object.

**Sending.** `ZDOMan.SendZDOToPeers2` starts a round-robin pass once `m_sendTimer > 0.05f`, so
each peer is serviced at most 20 times a second. `SendZDOs` budgets `10240 - GetSendQueueSize()` bytes and bails under 2048.

**Selection.** `CreateSyncList` has two paths. On the **client** (our host client feeding the
dedicated server) it walks `m_clientChangeQueue` with **no distance filter and no per-object rate limit**. On the **server** it gathers `FindSectorObjects(zone, peer.m_simulationDistance, …)` and filters through `ZDOPeer.ShouldSend`, which is purely revision-based: `return zdo.DataRevision > value.m_dataRevision`.

**Priority.** `ServerSortSendZDOS` sets `m_tempSortValue = Vector3.Distance(position, refPos)`
then subtracts `num * 1.5f`, `num` being seconds since last sync clamped to 100, so age buys at most 150 "metres". `ClientSortSendZDOS` sets the distance term to `0f`: on the client path ordering is **oldest-first only**, with `ObjectType.Prioritized` jumping the queue. Distance does not influence the client's send order at all.

**Payload.** `ZDO.Serialize` writes a flag word, prefab hash, rotation if non-identity, then
every float, Vec3, quaternion, int, long, string and byte array the object holds, each prefixed by a 4-byte hash. No per-field dirty tracking exists in the class.

Computed from the capture (208 MB = 218,103,808 B over 4,320 s):

| prefab | share | serializations | bytes/send | sends/s |
| --- | --- | --- | --- | --- |
| Player | 20.6 % | 78,869 | 570 | 18.3 |
| Boar | 12.2 % | 112,700 | 236 | 26.1 |
| Greydwarf | 10.5 % | 109,638 | 209 | 25.4 |
| Fish1 | 7.8 % | 186,566 | 91 | 43.2 |
| Fish5 | 7.4 % | 176,757 | 91 | 40.9 |
| Crow | 2.4 % | 65,847 | 79 | 15.2 |

Player at 18.3 sends/s against a 20 Hz ceiling independently confirms the model. These are `ZDO.Serialize` payload bytes only; `SendZDOs` prepends ~38 B of uid, revisions, owner and position, so true wire cost runs ~40 % above the table for fish.

Fish are physics bodies driven by `m_body.AddForce` every `CustomFixedUpdate` with `m_body.useGravity = false`, so they never settle, and `Fish.CustomFixedUpdate` has no player-distance gate. A fish 200 m from every player still costs full cadence inside the active area. INFERRED (prefab assets unreadable here): fish and birds carry `ZSyncTransform`; their own scripts write only `s_escape`, `s_hooked`, `s_spawnPoint`, `s_landed`, all rare, so the per-frame dirtying can only come from the transform component.

## Why the birds look wrong

VERIFIED mechanism, REPORTED symptom. `RandomFlyingBird.CustomFixedUpdate` advances `base.transform.position += base.transform.forward * num3 * dt` at `m_speed = 10f`. It moves the transform directly, so there is no Rigidbody velocity, and `ZSyncTransform.GetVelocity()` returns `Vector3.zero` with no body and no projectile. The bird publishes **zero velocity**.

On the non-owner, `SyncPosition` computes `position += vec2 * m_targetPosTimer` with that zero, i.e. **no extrapolation**, then `Vector3.Lerp(base.transform.position, position, 0.2f)` per fixed step. At 50 Hz that is a ~0.1 s lag constant, so the remote bird trails ~1 m behind a 10 m/s target and catches up in bursts as updates land. Arrivals are irregular because the client sort is oldest-first across every dirty object competing for a 10 KB budget. Rotation is worse: banking changes fast and is chased by `Quaternion.Slerp(…, 0.5f)`.

Second, independent cause: `SetVisible(m_nview.HasOwner())` parks the `LODGroup` reference point at `(999999, 999999, 999999)` when the ZDO has no owner, and `ZDOMan.ReleaseNearbyZDOS` runs every 2 s doing `tempNearObject.SetOwner(0L)` once the owner leaves the active area. A bird can pop out and back during a handoff. `Fish.SetVisible` is identical.

So: **non-owner interpolation without extrapolation**, made visible by irregular arrival, plus
**LOD blanking during ownership handoff**. Not the landing logic, which fires at most every
`m_wpDuration = 4f` seconds.

Vanilla defect noted in passing, not ours to fix: `OwnerSync` writes relative velocity to `s_velHash` while `SyncPosition` reads `s_velRelHash`. Affects `m_characterParentSync` only.

## Animals (VERIFIED)

`Character` calls `m_zanim.SetFloat(s_forwardSpeed, …)`, `s_sidewaySpeed`, `s_turnSpeed` and several `SetBool`s every movement update. `ZSyncAnimation.SetFloat` gates on `Mathf.Abs(m_animator.GetFloat(hash) - value) < 0.01f` then writes `m_nview.GetZDO().Set(438569 + hash, value)`, another `IncreaseDataRevision`. Raising that threshold saves nothing while the animal walks, because the position write already dirtied the object that frame. It helps only near-stationary animals, which is exactly tamed boars at a base: those still twitch at 236 B a send. `AddSessionHash` puts animator keys in `ZDOExtraData.s_sessionOnly`, so they are excluded from the save and throttling them cannot corrupt a world file.

## Prior art

- **BetterNetworking** (`bn/`, VERIFIED). `BN_Patch_UpdateRate` scales the `dt` into
`SendZDOToPeers2` by 0.75 or 0.5, lowering the global cycle to 15 or 10 Hz for everything including players. `BN_Patch_QueueSize` lies to `GetSendQueueSize` to enlarge the budget. `BN_Patch_Compression` wraps packets in Zstd. All global, none per-prefab; its own config text admits the cost ("If your character is lagging for others, decrease your update rate").
- **FiresGhettoNetworking** (REPORTED). Claims ZDO delta compression, distance throttling that
fires only while a connection backs up ("loose objects beyond the throttle distance (350 / 500 / 700 m by tier) go last"), and AoI-filtered RPC. Nearest neighbour to C1, but it is congestion-triggered reordering, not a steady-state cap, and its delta compression changes the wire format, which is out of bounds for us.
- **Valheim Community Patch** (REPORTED). ZDO work is allocation and lookup only: "Fix ZDO Packet
Allocation", "Fix Doubled ZDO Lookups", "Fix ZDO Value Write Allocation". Complementary.
- **ValheimPlus** (`vplus/`, VERIFIED): no `ZDOMan`, `ZDO`, `ZSyncTransform` or `ZSyncAnimation`
patch exists in the tree. Not prior art.
- **Iron Gate** (REPORTED, secondary sources only): raised data rate, added ZDO packet
compression. No per-prefab cadence control described. Not confirmed against official notes.

## Ranked candidates

All preserve wire format, ownership, persistence and save contents. Quantization excluded.

| # | candidate | mechanism | expected saving | risk |
| --- | --- | --- | --- | --- |
| C1 | Per-prefab min resend interval for cosmetics | Postfix `ZDOMan.CreateSyncList`; drop a ZDO from `toSync` while `Time.time - peer.m_zdos[uid].m_syncTime` is under the prefab's interval. Fish1/2/5, Crow, Seagal at 5 Hz. | 20.3 % of bytes in scope; ~15 % of total if per-instance cadence is really ~20 Hz | low. Deferring is lossless. Fish carry real velocity so extrapolation covers the gap; birds do not, so their jitter worsens until C3 lands. |
| C2 | Distance-gated interval for AI creatures | Same hook, for Boar/Greydwarf/Deer/Neck/Skeleton beyond 32 m of every peer ref pos and not in combat, 5 Hz. | ~44 % of bytes in scope; realised saving depends on how many are far | medium. Must never gate a creature targeting a player. |
| C3 | Publish bird velocity | Give the bird's `ZSyncTransform` a velocity so non-owners extrapolate. Fixes jitter and makes C1 safe for birds. | none directly; unblocks C1 on birds | medium. Changes remote motion; verify landing reads clean. |
| C4 | Debounce LOD blanking | Delay `SetVisible(m_nview.HasOwner())` by one `ReleaseZDOS` period (2 s). | none | low, but a visible behaviour change. |
| C5 | Raise `ZSyncAnimation.SetFloat` threshold | 0.01 to ~0.05 for non-player characters. | small; idle animals only | low. Coarser blending. |
| C6 | Gate `Fish.CustomFixedUpdate` beyond 80 m | Skip swim forces, sleep the body. | large, but a simulation change | high. Outside the stated bounds; listed for completeness, not recommended. |

Order: C1 on fish, then C3, then C1 extended to birds, then C2. C6 last or never.

Implementation caveat for C1/C2: `ZDOMan.ZDOPeer` is a private nested type, so the postfix must take it as `object` and reach `m_zdos` through `AccessTools`. Verify Harmony accepts that binding before costing the work.

## Falsification

`docs/attribution-telemetry.md` notes the report script ignores the `attributions` array, so these comparisons read raw capture JSON.

1. **Baseline.** Re-capture the two-player scenario unmodified; record per-prefab `bytes` and
`count`. Without it nothing can be attributed (perf-claim-requires-ab).
2. **C1 kill test.** Fish interval only. PASS needs Fish1+Fish2+Fish5 `bytes` down by roughly the
cadence ratio while Player `bytes` and `count` stay flat. If Player traffic rises, freed budget was absorbed by other objects and total bandwidth did not improve: the candidate fails.
3. **Lossless check.** Fish `count` falls with no teleport, stall or desync, and fishing still
works end to end: hook, `s_escape` pulse, `RPC_RequestPickup`, item in inventory.
4. **C2 kill test.** A creature inside 32 m shows unchanged `bytes`/`count`, and time from first
`SetTarget` to first hit is unchanged against baseline.
5. **Save invariance.** Diff the world file before and after a throttled session.
6. **C3/C4.** Birds land, take off and drop items normally; no LOD popping.

## What not to do

Do not change `ZDO.Serialize`, do not quantize, do not touch `DataRevision`/`OwnerRevision` accounting, do not throttle `ObjectType.Prioritized` or anything a peer owns, and do not lower the global `SendZDOToPeers2` rate: that is BetterNetworking's approach and it taxes players equally. Must not break fishing, bird landing and item drops, tamed animal behaviour and procreation, combat aggro timing, or save contents.

## Telemetry that would settle the open questions

The table cannot separate "few instances at 20 Hz" from "many instances at 4 Hz", and that decides whether C1 is worth 15 % or 3 %. Three bounded additions, all fitting the existing `MetricBook` with no retained per-object state:

1. **Per-prefab resend interval histogram**: bucket `Time.time - peer.m_zdos[uid].m_syncTime` at
`SendZDOs`. Answers per-instance cadence directly.
2. **Distance-at-send histogram per prefab**: `Vector3.Distance(zdo.GetPosition(), peer.GetRefPos())`
at send time. Sizes C2 before any code is written.
3. **Owner changes per prefab**: count `ZDO.SetOwner` transitions per interval. Tests the handoff
half of the bird explanation and prices C4.

## Sources

- Decompiled Valheim 1.0.12: `ZDOMan.cs`, `ZDO.cs`, `ZSyncTransform.cs`, `ZSyncAnimation.cs`,
`ZNetView.cs`, `Fish.cs`, `RandomFlyingBird.cs`, `Character.cs`, `ZNetScene.cs`, `SimulationDistance.cs`, `ZDOExtraData.cs`.
- Decompiled BetterNetworking: `BN_Patch_UpdateRate.cs`, `BN_Patch_QueueSize.cs`,
`BN_Patch_SendRate.cs`, `BN_Patch_Compression.cs`.
- [FiresGhettoNetworking](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresGhettoNetworking/),
[Valheim Community Patch](https://github.com/MidnightsFX/Valheim-Community-Patch), [Better Networking](https://www.nexusmods.com/valheim/mods/1570), [Valheim Performance Optimizations](https://www.nexusmods.com/valheim/mods/1360)
