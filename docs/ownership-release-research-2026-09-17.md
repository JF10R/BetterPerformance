# Net-change ownership release: design research 2026-09-17

Research only. No game launched, no code changed, no plugin deployed. Game methods and fields are named from a read of the installed Valheim 1.0.12 assemblies; no game source is copied here. VERIFIED = read in the named method this session. DERIVED = follows from what was read. ASSUMED = not verified.

## Verdict

Feasible and invariant-preserving, with one honest deviation. A whole release cycle runs synchronously inside one `ZDOMan.Update` call, before `SendZDOToPeers2`, so every intermediate owner write a cycle makes is invisible on the wire. Staging the cycle and applying only net transitions therefore cannot change what any peer observes, only how many owner revisions are burned. The deviation is that `OwnerRevision` grows more slowly than vanilla. Expected reduction in owner changes: at most one write per ZDO that is released and reclaimed inside the same cycle, and that share is currently unmeasured, so the design must not ship before the counter split in section 2 says the churn exists. Risk: medium, concentrated in the transpiler, not in the semantics.

## 1. The verified release algorithm

**Cadence.** `ZDOMan.Update` calls `ReleaseZDOS(dt)` only under `if (ZNet.instance.IsServer())`. `ReleaseZDOS` accumulates `m_releaseZDOTimer += dt` and returns unless `m_releaseZDOTimer > 2f`, then resets it to 0. (VERIFIED: `ZDOMan.Update`, `ReleaseZDOS`.)

**The passes.** One cycle is `ReleaseNearbyZDOS(ZNet.instance.GetReferencePosition(), m_sessionID)` first, then `ReleaseNearbyZDOS(peer.m_peer.m_refPos, peer.m_peer.m_uid)` for each `ZDOPeer` in `m_peers`, in list order. `ReleaseZDOS` and `ReleaseNearbyZDOS` have exactly one and two call sites respectively, all inside this pair, so the cycle is a closed unit. (VERIFIED: `ReleaseZDOS`; call-site counts from a whole-assembly reference scan.)

**One pass.** `zone = ZoneSystem.GetZone(refPosition)`; `FindSectorObjects(zone, new SimulationDistance(NearSimulationDistance, 0, IsClassic), m_tempNearObjects)` collects the near-simulation sectors around that zone. For each ZDO, non-persistent ones are skipped, then exactly two branches fire:

- release: `if (GetOwner() == uid && !ZNetScene.InActiveArea(position, zone)) SetOwner(0L);`
- claim: `else if ((!HasOwner() || !IsInPeerActiveArea(position, GetOwner())) && ZNetScene.InActiveArea(position, zone)) SetOwner(uid);`

(VERIFIED: `ReleaseNearbyZDOS`, `FindSectorObjects`, `IsInPeerActiveArea`, `ZNetScene.InActiveArea` / `PointInsideActiveArea`.)

**Both branches always write a real change.** The release branch runs only when the owner equals `uid`, which is non-zero, so writing 0 changes it. The claim branch runs only in the `else`, so the owner already differs from `uid`. `ZDO.SetOwner` guards on `ZDOExtraData.GetOwner(m_uid) != uid` before `SetOwnerInternal` plus `IncreaseOwnerRevision`, and that guard can never reject a write from this method. (VERIFIED: `ZDO.SetOwner`, `SetOwnerInternal`.) DERIVED: on a dedicated server, where the other `SetOwner` call sites are client-side gameplay code, most of the 37 calls per second are real revision bumps from this loop.

**Release then reclaim inside one cycle.** A ZDO owned by peer A, standing in B's active area but no longer in A's, is written twice when A's pass comes before B's: A releases it to 0, B claims it. With the pass order reversed, B's claim branch sees `!IsInPeerActiveArea(position, A)` and writes once. Same final owner, one or two writes depending only on `m_peers` order. The server pass is always first and takes the same form with `uid = m_sessionID`. DERIVED from the two branches above.

**Overlapping active areas do not flap.** For a ZDO owned by A and inside both areas, A's pass fails the release test and B's pass fails the claim test, because `IsInPeerActiveArea(position, A)` holds. VERIFIED by the branch conditions. So a shared base is not churning on shared pieces; it churns on the boundary ring where one player's area has moved off an object the other still covers.

**Where the 4.0 M unchanged grants come from, and it is not this loop.** `ownership_grants_skipped_unchanged` is incremented by `OwnershipExpedite.AfterSetOwner`, a postfix on `ZDO.SetOwnerInternal` that only observes while the thread-static `incoming` flag set by the `ZDOMan.RPC_ZDOData` prefix is true. `RPC_ZDOData` calls `SetOwnerInternal(ownerInternal)` unconditionally in its full-update branch, on every received ZDO whose data revision advanced. So that counter measures redundant owner re-application on the replication receive path, driven by position and data updates, and it is unrelated to release churn. (VERIFIED: `src/BetterPerformance/OwnershipExpedite.cs:116-176`; `ZDOMan.RPC_ZDOData` owner-apply sites.)

**The server's own pass scans the world origin.** Every `ZNet.SetReferencePosition` call site is client-side: `Player`, `Game.FindSpawnPoint`, `Tracker`, `Valkyrie`. DERIVED: on a dedicated server none of them runs, `m_referencePosition` keeps its default, and the server pass walks the near sectors around (0,0,0). It is therefore usually disjoint from the players' base and contributes little churn there, but it is not a no-op and must be staged like any other pass.

## 2. Are the 37 per second net changes or churn? Unresolved, and here is the proof

Not answerable from the present counters. `zdo_set_owner_calls` is a parameterless prefix on `ZDO.SetOwner` (`src/BetterPerformance/OwnershipTelemetry.cs:36,103`): it counts calls from every caller, cannot see whether the write changed anything, and cannot attribute a call to the release loop. Proposed split, all server-side, all keyed by caller with the same thread-static technique the expedite module already uses:

| Counter | Source | What it settles |
| --- | --- | --- |
| `zdo_set_owner_calls_release` | `SetOwner` prefix while the release-cycle flag is set | release loop share of the 37/s |
| `zdo_set_owner_calls_other` | same prefix, flag clear | everything else, including `RPC_RequestOwn` |
| `zdo_set_owner_noops` | `SetOwner` prefix where `GetOwner() == uid` | whether any counted call is not a change |
| `release_cycle_releases` | release branch writes per cycle | volume of `SetOwner(0)` |
| `release_cycle_claims` | claim branch writes per cycle | volume of reassignment |
| `release_cycle_reclaimed` | ZDOs released in pass i and claimed in pass j>i of the same cycle | the churn the design removes |
| `release_cycle_net_changes` | ZDOs whose owner at cycle end differs from cycle start | the floor no design can go below |
| `release_cycle_scanned`, `release_cycle_ms` | per cycle | scan cost, for the ValheimPerformanceOptimizations comparison |

Decision rule: the design is worth building only if `release_cycle_reclaimed` is a material fraction of `release_cycle_releases`. If `release_cycle_releases + release_cycle_claims` is already close to `release_cycle_net_changes`, there is nothing to remove and the 37/s come from elsewhere, which `zdo_set_owner_calls_other` will then name. These counters are observation-only and can ship before any behavior change, exactly as the expedite observers did.

## 3. The net-change design

**Hook shape.** Two patches, both on `ZDOMan`, both installed only when `ZNet.instance.IsServer()`.

1. Prefix plus finalizer on `ReleaseZDOS(float)`: the prefix opens a cycle (clears the staging map, sets a thread-static flag); the finalizer applies the staged net transitions and closes the cycle. A finalizer, not a postfix, so a throwing pass cannot leave the flag set.
2. Transpiler on `ReleaseNearbyZDOS(Vector3, long)`, redirecting exactly three member calls inside that method body: `ZDO.GetOwner` (2 sites), `ZDO.HasOwner` (1 site), `ZDO.SetOwner` (2 sites), to shadow-aware statics `Shadow.GetOwner(zdo)`, `Shadow.HasOwner(zdo)`, `Shadow.Stage(zdo, uid)`.

The shadow reads must be redirected too, otherwise a later pass in the same cycle would read the unstaged owner and diverge. `IsInPeerActiveArea` needs no patch: it receives the owner value the redirected `GetOwner` returned.

**Computing the final owner.** `Stage` writes `staged[zdo.m_uid] = uid` and never touches the ZDO. `Shadow.GetOwner` returns the staged value if present, else `zdo.GetOwner()`; `Shadow.HasOwner` returns `Shadow.GetOwner(zdo) != 0`. At the finalizer, for each staged entry in insertion order: re-resolve with `ZDOMan.GetZDO(id)`, skip if it is gone or is not the same instance, else call the native `ZDO.SetOwner(target)`, whose own idempotence guard drops the write when the owner already matches.

**Invariants.**

- Final owner per ZDO after the cycle is identical to vanilla. The shadow reproduces every input the vanilla branches read (pass `uid`, pass `zone`, ZDO position, ZDO owner), in the same pass order, so it computes the same last write. A ZDO can be released in one pass and claimed in a later one, but never claimed and then released, because a release requires a later pass whose `uid` equals the claimer, and each id gets exactly one pass.
- No ZDO stays ownerless longer than vanilla. Vanilla's ownerless interval is bounded by the cycle, which completes inside one `Update` before `SendZDOToPeers2` and `SendDestroyed`; the design's interval is zero.
- Revisions and sends are unchanged for unchanged owners: a ZDO no pass touches is not staged and not written, exactly as in vanilla.
- Server-only: the loop itself is inside `if (ZNet.instance.IsServer())`, and the module refuses to install otherwise.
- Fail closed: the transpiler asserts the exact site counts above and the prefix/finalizer pair asserts the two `ReleaseNearbyZDOS` call sites; any mismatch leaves the game unpatched, the pattern already used by `OwnershipExpedite.ValidateContracts`.

**The one deviation, stated plainly.** A churned ZDO gets one `IncreaseOwnerRevision` per cycle instead of two. `ZDOPeer.ShouldSend` compares revisions for inequality, so delivery is unaffected, but the server's revision lead over a client that is independently bumping the same ZDO narrows by one per avoided churn. `RPC_ZDOData` accepts an incoming owner only on `num3 > zDO.OwnerRevision`, so a narrower lead is theoretically easier for a client to overtake. ASSUMED low impact; it is the first thing a two-client test should look for.

**Edge cases.**

- Peer disconnect mid-cycle: not reachable. `ReleaseZDOS` runs to completion inside one `Update` with no network pump between passes, and `RemovePeer` runs from the socket pump. The finalizer still writes the staged target even if it names a peer, which is what vanilla would have written.
- ZDO destroyed mid-cycle: likewise not reachable in-frame; the `GetZDO` re-resolve at flush is a belt-and-braces guard, not a correctness requirement.
- Boundary objects: the predicates are unchanged and pure in `(position, zone)`, and both `peer.m_peer.m_refPos` in the pass and `peer.GetRefPos()` inside `IsInPeerActiveArea` read the same field, so a boundary object resolves identically in shadow and vanilla.
- Server reference-position pass: staged like any other, first in order, `uid = m_sessionID`.
- `ZNet.GetPeer(uid)` returning null inside `IsInPeerActiveArea` makes it false and enables a claim. Unchanged: the design does not touch that call.

**What could break.** AI and physics hand-off and item pickup all key off the final owner, which is identical, so the semantic surface is the transpiler itself: a missed redirect site would mix shadow writes with live reads and could hand an object to the wrong peer. That is why the site counts are asserted exactly and the patch refuses to install on any mismatch. A second, smaller hazard is a future game version adding a third `SetOwner` site or a caller of `ReleaseNearbyZDOS` outside `ReleaseZDOS`; the contract check catches both.

## 4. Alternative: hysteresis, and why it is not safe

Hysteresis would require a ZDO to fail the active-area test for N consecutive cycles before the release branch writes 0. It is cheaper to implement and it is a behavior change, not a reordering. A ZDO left owned by a peer whose active area has moved off it is owned by a client that has already destroyed its local instance, so it is unsimulated while owned, and `ZNetView.InvokeRPC` targets `m_zdo.GetOwner()`, which means an `RPC_RequestOwn` from a second player is routed to a client with no view to answer it. Vanilla's 2-second release is what ends that window. Lengthening it deliberately extends the exact failure the plugin is trying to shorten. The claim branch masks this whenever another player is nearby, which makes the regression intermittent and hard to attribute, which is worse. Reject.

## 5. What a disposable-world two-client test would need to prove

1. Counter-split capture first, with no behavior change: `release_cycle_reclaimed` materially above zero. If it is near zero, stop here.
2. Owner-identity equivalence: two clients at a shared base walking the boundary ring, capturing the per-cycle final owner map with the design off and on under the same route. The pass-order dependence means only an identical route gives a fair comparison; an offline replay of a recorded sector asserting a byte-identical post-cycle owner map is the stronger form and needs no second client.
3. `zdo_set_owner_calls_release` falls and `zdoman_zdos_sent_last_sec` falls with it. A fall in the first alone means release churn was not consuming the replication budget, which refutes the value of the change without refuting its safety.
4. Pickup latency: the ownership-gated wait distribution for the second player, against the expedite-only baseline, on the same world. No change is an acceptable result here; a regression is not.
5. Watch for owner-revision rejection: any ZDO whose owner appears to revert, or any stuck unowned object, after a client-side ownership change on a churned object.

Contract tests belong beside the existing ones in `tests/BetterPerformance.GameTests/OwnershipExpediteGameTests.cs`: assert the transpiler site counts, the release/claim branch shapes, and that the staging map is empty after the finalizer.
