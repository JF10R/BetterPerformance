# Ownership churn and second-player loot latency

Research snapshot: 2026-09-15. Research only. No game was launched, no code changed, no plugin deployed. Decompiled Valheim 1.0.12 (ILSpy, exact installed build) was read for this review; no game source was copied into the repository. VERIFIED = read here, in the file and method named on the line. REPORTED = someone else's claim, not reproduced here.

## Verdict

The second player's pickup is slow for a structural reason, not a bandwidth accident: **the item pickup path has no reply RPC and no forced ZDO send, while the container path has both.** A container open is answered directly by `RPC_OpenResponse` and its ZDO is queue-jumped by `ForceSendZDO`; a pickup waits for an ordinary, sorted, byte-budgeted replication of the owner change, then for a 50 ms client poll. That asymmetry explains 15 ms mean container waits beside 530 ms ownership-gated pickups in the same capture.

The best-value change is server-side and invariant-free: **expedite delivery of an owner change to its new owner**, giving it the queue priority the container path already has. The second is **cutting redundant owner transitions in `ReleaseNearbyZDOS`**, which consume the replication budget the pickup must pass through. Dead-ZDO trimming is not a latency fix and is a duplication hazard; reject it.

One premise correction: `container_open_requests = 139, direct = 0` is expected by construction, not evidence about who owns the host's chests. `Container.Interact` has no local fast path — it always calls `m_nview.InvokeRPC("RPC_RequestOpen", playerID)`. When the host owns the chest, `ZRoutedRpc.InvokeRoutedRPC` sees `targetPeerID == m_id` and runs `HandleRoutedRPC` in the same frame, zero hops. The 15 ms mean is consistent with mostly-local handling. (VERIFIED: `Container.cs` `Interact`; `ZRoutedRpc.cs` `InvokeRoutedRPC`.)

## Mechanism

**Routing to another client is two hops — VERIFIED from source, since no fetched web page states it.** Three facts compose. First, a client holds exactly one peer: both client entry points construct `new ZNetPeer(socket, server: true)` and the `m_hostSocket.Accept()` path that adds any other peer is server-only (`ZNet.cs:756` `Connect`, `ZNet.cs:829` `UpdateClientConnector`, `ZNet.cs:1679`, `ZNet.cs:843` `OnNewConnection`). Second, the non-server branch of `RouteRPC` broadcasts to all of its peers, which for a client is only the server: `foreach (ZNetPeer peer3 in m_peers)` (`ZRoutedRpc.cs:166`), reached because the `if (m_server)` guard at `ZRoutedRpc.cs:144` is false. Third, the server forwards it: `if (m_server && routedRPCData.m_targetPeerID != m_id) { RouteRPC(rpcData); }` (`ZRoutedRpc.cs:183` in `RPC_RoutedRPC`), and the server branch then unicasts to the named peer. `ZNetView.InvokeRPC(method, ...)` targets `m_zdo.GetOwner()`. So her → host is her → server → host.

**Pickup path.** `ItemDrop.Pickup` on a non-owned item starts a poll and asks for ownership: `InvokeRepeating("PickupUpdate", num, num)` with `num = 0.05f`, then `RequestOwn()`. The host's handler grants it and stops: `if (m_nview.IsOwner()) { m_nview.GetZDO().SetOwner(uid); }` — no reply, no forced send. `PickupUpdate` only re-tests `CanPickup()`, which is `return m_nview.IsOwner()`. (VERIFIED: `ItemDrop.cs` `Pickup`, `RequestOwn`, `RPC_RequestOwn`, `PickupUpdate`, `CanPickup`.)

**Container path, for contrast.** `RPC_RequestOpen` does both things the pickup path omits: `ZDOMan.instance.ForceSendZDO(uid, m_nview.GetZDO().m_uid);` then `SetOwner(uid)` then `m_nview.InvokeRPC(uid, "RPC_OpenResponse", true)`. The GUI opens on the reply, not on the ZDO. (VERIFIED: `Container.cs` `RPC_RequestOpen`, `RPC_OpenResponse`.)

**Why forced sends are fast.** `AddForceSendZdos` does `syncList.Insert(0, zDO)` — it jumps the sorted queue. Ordinary sends do not. (VERIFIED: `ZDOMan.cs` `AddForceSendZdos`.)

**Replication is quantized and byte-budgeted.** `SendZDOToPeers2` gates on `m_sendTimer > 0.05f`, then serves one peer per frame. `SendZDOs` refuses below a floor — `if (!flush && sendQueueSize > 10240) return false;` and `int num = 10240 - sendQueueSize; if (num < 2048) return false;` — and truncates the list with `if (zPackage.Size() > num) break;`. (VERIFIED: `ZDOMan.cs` `SendZDOToPeers2`, `SendZDOs`.)

**Any owner change costs a full ZDO resend to every peer.** `ZDO.SetOwner` is idempotent (`if (ZDOExtraData.GetOwner(m_uid) != uid)`) but a real change calls `IncreaseOwnerRevision()`, and `ZDOPeer.ShouldSend` returns true on any `OwnerRevision` increase. So the measured ~20 SetOwner/s are ~20 real revision bumps/s, each re-serializing a whole ZDO to each of the two peers. (VERIFIED: `ZDO.cs` `SetOwner`, `IncreaseOwnerRevision`; `ZDOMan.cs` `ZDOPeer.ShouldSend`.)

**Where the 20/s comes from.** Server-only `ReleaseZDOS` runs on `m_releaseZDOTimer > 2f` and calls `ReleaseNearbyZDOS` once for the server reference position and once per peer. Each pass walks every persistent ZDO in the near-simulation sectors and either releases (`if (!ZNetScene.InActiveArea(position, zone)) { tempNearObject.SetOwner(0L); }`) or claims it for that peer. Both branches only fire on a real transition. At a shared base the two peers' active areas overlap thousands of pieces, and boundary objects release and re-claim across passes inside the same 2 s cycle. (VERIFIED: `ZDOMan.cs` `Update`, `ReleaseZDOS`, `ReleaseNearbyZDOS`.)

**Disconnect is a whole-world scan, and persistent objects are not released there.** `ZDOMan.RemovePeer` calls `RemoveOrphanNonPersistentZDOS`, which iterates **all** of `m_objectsByID` — not a sector — and destroys every non-persistent ZDO whose owner is gone: `if (!value.Persistent && (!value.HasOwner() || !IsPeerConnected(value.GetOwner()))) { value.SetOwner(m_sessionID); DestroyZDO(value); }`. Persistent objects owned by the departing peer are left alone and reclaimed lazily on the next `ReleaseNearbyZDOS` cycle by its `!IsInPeerActiveArea(...)` branch. Every destroy in that sweep also adds a tombstone, so a disconnect is a step increase in `zdoman_dead_zdos`. (VERIFIED: `ZDOMan.cs:827` `RemovePeer`, `ZDOMan.cs:1458` `RemoveOrphanNonPersistentZDOS`, `IsPeerConnected`.)

**Dead ZDOs are never trimmed in-session.** `m_deadZDOs[uid] = ticks` on every server-side destroy; the only `m_deadZDOs.Clear()` calls are in `LoadChunks` and `Load`. No cap, no age purge. Its sole read is the resurrection guard `if (ZNet.instance.IsServer() && flag && m_deadZDOs.ContainsKey(zDOID)) { zDO.SetOwner(m_sessionID); DestroyZDO(zDO); }` — an O(1) hash probe, only on receipt of a ZDO the server does not have. 26,347 entries is roughly 0.6 MB and no measurable time. (VERIFIED: `ZDOMan.cs` every `m_deadZDOs` line, `RPC_ZDOData`.)

**`ConvertOwnerships` is irrelevant here.** It runs once at load, only for `version < Version.World.NewSaveFormat`, migrating ship and fishing-rod owner fields. It is not a runtime reassignment path. (VERIFIED: `ZDOMan.cs` `ConvertOwnerships` and its version-gated call site in `Load`.)

## Timeline: she picks up an item the host owns

| Step | Where | Cost |
| --- | --- | --- |
| 1. `Pickup` → `RequestOwn` → routed RPC | her frame | frame quantum |
| 2. relay to host | server receive pump + `RouteRPC` | link + **server hitch lands here** |
| 3. `RPC_RequestOwn` → `SetOwner(uid)` → `ClientChanged` | host frame | frame quantum |
| 4. host `SendZDOToPeers2` → server | host | 0–50 ms + **host hitch lands here** (saves 113–266 ms, terrain 50–100 ms frames) |
| 5. server applies, queues for her | server | sorted behind churn, byte-budgeted |
| 6. server `SendZDOToPeers2` → her | server | 0–50 ms + **server hitch lands here** |
| 7. `PickupUpdate` next tick sees `IsOwner()` | her frame | 0–50 ms poll |

Four network legs, two 50 ms send quanta, one 50 ms poll quantum, three frame quanta. A LAN floor near 100 ms, and a single host save at step 4 or a full send queue at step 6 accounts for the 530 ms observations without needing any other explanation.

**Retry trap.** `PickupUpdate` never re-calls `RequestOwn`. A lost grant leaves the item stuck until she presses again, and that press is swallowed if it lands inside `m_ownerRetryTimeout = Mathf.Min(0.2f * Mathf.Pow(2f, m_ownerRetryCounter), 30f)`. The counter only resets in `CanPickup` once she is owner. (VERIFIED: `ItemDrop.cs` `RequestOwn`, `PickupUpdate`, `CanPickup`.)

## Prior art

**BetterNetworking does not touch ownership — VERIFIED**, closing the point its own documentation left unconfirmed. Across the decompiled `bn/` tree its Harmony targets are `ZNet`, `ZSteamSocket`, `ZPlayFabSocket`, `PlayFabZLibWorkQueue`, `FejdStartup`, and exactly two `ZDOMan` members: `AddPeer` (connection buffering, the "ZDO buffer") and `SendZDOToPeers2`. The latter can only *slow* replication — `BN_Patch_UpdateRate` multiplies `dt` by 0.75 or 0.5, never above 1.0. `BN_Patch_QueueSize` postfixes `ZSteamSocket.GetSendQueueSize` to under-report by up to 71,680 bytes, which widens ZDOMan's effective 10,240-byte budget (32 KB by default). No `SetOwner`, `RequestOwn` or `RPC_RequestOpen` patch exists in it.

**ValheimPlus does not reassign ownership — VERIFIED.** Only three files in the decompiled `vplus/` tree mention `ZDOMan` or ownership, and the one substantive use is `InventoryAssistant.NearbyChestsLoaded` calling `ZDOMan.instance.FindSectorObjects` for discovery. No `SetOwner` call.

**ValheimCommunityPatch — REPORTED.** Ships `Fix Disconnect ZDO Sweep` (server indexes non-persistent ZDOs by owner so a disconnect sweeps only that player's objects), plus `Fix ZDO Packet Allocation`, `Fix ZDO Value Write Allocation`, `Fix Doubled ZDO Lookups`, `Fix Prefab Query Scan`, `Tolerate Duplicate ZDOs On Load`, `Fix Fuel And Ore Loss` (client claims ownership before adding fuel or ore) and `Refund Rejected Station Items`. No RPC-routing change documented; regressions are self-reported only. The disconnect item is independently corroborated by the whole-world `m_objectsByID` scan verified above, and it is real but off our critical path: it is a disconnect-time cost, not a steady-state pickup cost. [Thunderstore](https://thunderstore.io/c/valheim/p/MidnightMods/ValheimCommunityPatch/), [source](https://github.com/MidnightsFX/Valheim-Community-Patch).

**ValheimPerformanceOptimizations — REPORTED.** Claims it "does not change vanilla behavior at all" while listing **faster server-side ZDO ownership release scans** and VisEquipment ZDO caching alongside rewritten `ZNetScene` streaming. That is the same surface as candidate 2 below, but it optimizes the scan's cost rather than the number of owner transitions it emits, so the two are complementary rather than redundant. Its no-behavior-change claim is unverified here. [Source](https://github.com/KillerGoldFisch/ValheimPerformanceOptimizations).

**Server-owned and eager-ownership mods — REPORTED.** `Serverside Simulations` has the server create, own and simulate zones instead of the first client, with a documented tradeoff of *added* latency for the client that would otherwise have owned them — which is direct prior-art support for rejecting server-owned containers below. `ServersideQoL` assigns crafting-station ownership to the nearest player to prevent ore and fuel loss, with `MissingMethodException` issues #191 and #198 reported against it. No mod named `ServerSideOwnership` or `OwnershipFix` exists. [Serverside Simulations](https://thunderstore.io/c/valheim/p/mvp/Serverside_Simulations/), [ServersideQoL](https://thunderstore.io/c/valheim/p/ArgusMagnus/ServersideQoL/).

**Explicit negatives after genuine search.** No mod bypasses or short-circuits `ItemDrop.RequestOwn` / `RPC_RequestOwn`; the "instant loot" family only removes the monster loot-spawn delay and never touches ownership. No item duplication or item-loss report traced to an ownership race was found. No Iron Gate patch note or tracker entry on `m_deadZDOs` or `SetOwner` churn was found; the nearest adjacent work is `Less Zdo Corruption`, which documents unbounded per-zone spawn-timestamp lists and a `ZNetScene.RemoveObjects` stuck loop, neither of which is `m_deadZDOs`. Chest desync appears only in Steam forum threads and ValheimPlus issue #675, none naming ownership. Candidate 4 below therefore appears to be unexplored ground rather than a known-bad idea, which is a reason to measure it, not a reason to trust it. [Less Zdo Corruption](https://thunderstore.io/c/valheim/p/ASharpPen/Less_Zdo_Corruption/), [ValheimPlus #675](https://github.com/valheimPlus/ValheimPlus/issues/675).

## Candidates

| # | Candidate | Mechanism | Her latency | Cost / risk | Falsifying experiment |
| --- | --- | --- | --- | --- | --- |
| 1 | **Expedite owner grants (server-side)** | On the server, when an applied ZDO update changes the owner to peer X, call `GetPeer(X).ForceSendZDO(id)` so `AddForceSendZdos` inserts it at index 0 for X | Removes leg-6 queueing; targets the 530 ms tail, not the floor | Low. Reordering only; no data, ownership, destruction or save-order change. One hash lookup per received owner change | A/B the ownership-path wait distribution on her pickups, server otherwise unchanged. Fails if p95 is unmoved, which would locate the delay in legs 2–4 instead |
| 2 | **Net-change `ReleaseNearbyZDOS`** | Compute all passes of one 2 s cycle into a target-owner map, apply only net transitions, skipping release-then-reclaim of the same ZDO | Indirect: fewer forced resends means more budget for everything, including step 6 | Low-medium. Final owner set must be byte-identical to vanilla per cycle; pass order dependence must be preserved | Offline: replay a recorded sector, assert the post-cycle owner map matches vanilla exactly. Runtime: `zdo_set_owner_calls` must fall *and* `zdoman_zdos_sent_last_sec` fall with it. Fails if SetOwner falls alone — then churn was not the bottleneck |
| 3 | **Widen the server send budget** | No new code: BetterNetworking's `QueueSize` on the server raises the effective per-interval budget from 10 KB toward 32 KB | Helps only if step 6 is truncating | Low, already in the baseline stack. BN's docs trade larger queues against responsiveness on a constrained uplink; weak on LAN | Instrument the `zPackage.Size() > num` break count and the queue-full early returns first. If both are ~0 this candidate is dead before it is tried |
| 4 | **Pickup reply RPC** | Mirror `RPC_OpenResponse`: host replies granted, her client completes without waiting for ZDO replication | Removes legs 5–6 and the poll; the largest theoretical win | **Requires the plugin on her PC.** Safe against duplication only because the `if (m_nview.IsOwner())` guard makes a second grant impossible, so that guard must never be relaxed. No prior art either way | Deferred until her plugin exists. Compare her `Pickup`-entry-to-`AddItem` against candidate 1 on the same world |
| 5 | **Shorten her `PickupUpdate` poll** | 50 ms → 16 ms | Up to 34 ms off the tail | Requires her plugin. Small, raises her per-item invoke rate | Only worth running bundled with 4 |

### Rejected

- **Proactive hand-off to a player walking toward an item.** Requires predicting intent. Granting ownership to someone who then walks away leaves a remote owner near the active-area edge — exactly the state `ReleaseNearbyZDOS` churns on, so it makes candidate 2 worse. Two players converging on one item produce grant and re-grant thrash. No duplication risk, since ownership stays single-writer, but negative expected value and unfalsifiable without her plugin.
- **Server-owned shared containers.** Would cut the container path to one hop and let `ForceSendZDO(peerID, id)` take its direct server branch. But the server's own `ReleaseNearbyZDOS` peer passes reclaim nearby ZDOs for whichever player is in the active area, so forced server ownership fights the release loop every 2 s. It also moves chest inventory writes and the `IsInUse` mutual exclusion onto the server, where a defect loses or duplicates chest contents. `Serverside Simulations` documents the same tradeoff from the other direction: moving simulation to the server adds latency for the client that would have owned the object. Rejected on risk against a 15 ms mean that is already acceptable.
- **Dead-ZDO trimming.** No path from `m_deadZDOs` to latency: one O(1) probe, ~0.6 MB. Removing a tombstone re-enables the exact case it guards — a client still holding a destroyed chest or item resurrects it on the server, which is item duplication. Rejected on integrity.
- **Widening or slowing the `ReleaseNearbyZDOS` radius or cadence.** Changing the radius changes which peer simulates what; lengthening the cadence leaves objects ownerless longer, stalling AI and physics handover. Both are behavior changes, not latency fixes.

## Counters to add

Without her plugin, all of these are server-side or host-side:

- **Split `zdo_set_owner_calls` by caller** — `ReleaseNearbyZDOS` release, `ReleaseNearbyZDOS` claim, routed-RPC grant, disconnect sweep, invalid-prefab destroy. One undifferentiated number cannot separate churn from grants, and candidate 2 is unfalsifiable without the split.
- **`SendZDOs` outcome per peer per interval** — bytes written, sync-list length, count truncated by the `zPackage.Size() > num` break, and early returns for a full queue. This is the decisive test for candidates 1 and 3.
- **Owner-change delivery age** — server timestamp when it applies an owner change, and when it first includes that ZDO in a package for the new owner's peer. Gives leg 6 from a single clock.
- **Routed-RPC relay pairing** — record `ZRoutedRpc.RoutedRPCData.m_msgID` at the host's `RPC_RequestOwn` and `RPC_RequestOpen` observers and at the server's relay. Yields legs 1–2 and the server's relay hitch with no clock join.

Only her plugin can measure: her `Pickup` entry to inventory acceptance, her poll phase, the arrival instant of leg 6 in her process, her own frame hitches, and whether a `RequestOwn` was actually sent or swallowed by the retry backoff.

Cross-process latency stays bounded by the clock caveat in [host-network-telemetry.md](host-network-telemetry.md): the QPC join between the two Unity processes is an untested hypothesis. `m_msgID` pairing gives ordering and per-process deltas, not a true round trip.
