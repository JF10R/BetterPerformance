# Mined-loot visibility latency — measurement plan, 2026-09-17

User question: on the dedicated server with two or more players, ore appears
noticeably after the vein chunk disappears. Reduce the delay. Concerns visibility,
not pickup.

## Where the delay can sit

All line numbers are vanilla 1.0.12 decompiled sources.

| Leg | Mechanism | Order of magnitude |
| --- | --- | --- |
| Hit to owner | `MineRock5.Damage` sends `RPC_Damage` to the ZDO owner (`MineRock5.cs:313`); only the owner drops (`MineRock5.cs:404-412`, guard at `:340`) | half an RTT |
| Owner to client, visual | `RPC_SetAreaHealth` to `Everybody` (`MineRock5.cs:406`), an immediate RPC | half an RTT |
| Owner to client, item ZDO | `ZDOMan.SendZDOToPeers2` (`ZDOMan.cs:889-905`): 0.05 s gate, then one peer per frame | 50-100 ms |
| Local creation | `ZNetScene` creates at 30 Hz (`ZNetScene.cs:11`), 10 objects per frame (`:187`) | 33 ms, more under backlog |

The two owner-to-client legs are the working hypothesis: the destruction RPC leaves
at once while the item ZDO waits for the send tick, so the chunk vanishes first by
construction. Send ordering is not the cause — `ServerSortSendZDOS` already gives a
never-synced ZDO a -150 sort bonus (`ZDOMan.cs:1363-1375`).

Pickup is a separate 0.5 s plus an ownership round trip (`ItemDrop.cs:1687`, `:1604`)
and is out of scope here.

## Why existing telemetry does not answer it

`loot_queue_*` observes the local creation queue only, sampled every 250 ms
(`LootQueueTelemetry.cs`, `LootQueueScanIntervalMilliseconds`). It cannot see the
network legs and its sampling is coarser than the effect.

## Which leg actually applies here

Ownership decides which case applies, and it varies. An observed network arrival proves
that the item entered through the network path, not who owned the nearby destruction.
`arrival_missing` means no arrival was retained: the item may be locally created, or its
network observation may have been skipped or expired. It does not prove local creation.

## Probe, implemented

`LootVisibilityTelemetry` plus `LootVisibilityTracker` in Core, exporting
`loot_visibility_*`. Since 0.4.10 t0 also comes from trees, logs, plain rocks and
destructibles with a drop table: the owner's `ZNetScene.Destroy(GameObject)` (only for an
owned ZDO — a non-owner reaching it is a zone unload, and `RemoveObjects` never calls it) and
the remote `ZNetScene.OnZDODestroyed(ZDO)`. A felled tree's visible result is its `TreeLog`,
so logs count as loot beside `ItemDrop`. `loot_visibility_destroyed_{rock,tree,log,destructible}`
count the t0 events per source, and `item_drop_instances` is the live dropped-item population
(each one a rigidbody the physics step pays for). Client side only, one clock — cross-process Stopwatch origins are
offset on this runtime (`tasks/lessons.md`). Three postfixes: `MineRock5.RPC_SetAreaHealth`
(t0, on every client whether or not it owns the rock), the private
`ZDOMan.CreateNewZDO(ZDOID, Vector3, int)` whose zero prefab hash is what distinguishes a
network arrival from local creation (t1), and `ZNetScene.AddInstance(ZDO, ZNetView)` (t2).

## 0.4.11: what the probe missed

The 4.5 h session of 2026-09-17 matched 1,083 drops and every one of them was
network-created: `locally_owned` and `arrival_missing` both read 0. t2 was a postfix on
`ZNetScene.CreateObject(ZDO)`, which only network-arrived ZDOs pass through, so drops this
client instantiated itself as owner never reached t2 and their destructions expired
unmeasured. That is the owner-side case the question started from.

t2 is now `ZNetScene.AddInstance(ZDO, ZNetView)`, which `ZNetView.Awake` calls for local
instantiation and network creation alike. This widened coverage but did not establish
valid timing for drops without a network arrival; the correction below excludes them.
`loot_visibility_destroyed_rock` read 0 all session while this client
handled 1,271 `RPC_Damage` on copper veins: it counted only the legacy `MineRock`
component, and now counts destroyed `MineRock5` areas too.

Attribution is positional: a destroyed rock is joined to a drop by distance and time, not
by identity, because the game records no link between them. t0 is the rock root rather
than the hit-area centre, which is why the radius cannot be small. Storage and scans are
bounded, but runtime overhead has not been measured.

Defaults: `LootVisibilityEnabled` true, radius 12 m, window 5 s.

### Attribution v2

`loot_visibility_attribution=network_arrival_single_candidate_v2` identifies the corrected
semantics. Vanilla 1.0.15 can instantiate a felled tree's logs and drops before destroying
the source. A local creation can therefore precede its own t0 and match an older nearby
destruction, inventing a long perceived delay. Earlier captures must not be interpreted as
proof of that delay or compared directly to v2 timing totals.

Only an observed network arrival with exactly one eligible destruction enters v2 timings.
Its source timestamp is frozen at arrival and cannot be replaced by a later destruction
or an overwritten ring slot. Missing arrivals, multiple eligible destructions and invalid
chronology are excluded from durations and the histogram. `loot_visibility_ambiguous`
counts ambiguous completions; the missing-arrival and chronology counters remain explicit.
All three timed legs describe the same eligible observations. Local creation latency is
unavailable, and even one positional candidate does not prove causality.

Offline tests cover missing arrivals, multiple sources, source changes, ring overwrite,
expiry, backwards chronology and reporting of legacy/v2 mixtures. No gameplay or pickup
behavior changes.

### Attribution v3 and send legs (0.4.14)

`loot_visibility_attribution=network_arrival_table_filtered_v3`. The 2026-09-23 session showed
v2's >1 s tail was partly pairing: in 7 of 11 slow windows this client owned the rock, whose
drops are local, and the timed network drop was the other player's. v3 therefore:

- takes a `MineRock5` t0 at the destroyed area's collider centre (a prefix, before `UpdateMesh`
  deactivates it), where `DamageArea` spawns the drops; the rock root is a counted fallback;
- marks a destruction owned when this process ran it (routed sender = own session id, or the
  owner's `ZNetScene.Destroy`); an owned source is never a network drop's candidate;
- keeps up to four candidates per arrival and, at t2, only those whose drop table holds the
  item and whose kind radius covers the arrival: area max(4 m, half-diagonal + 2 m), tree/log/
  destructible from their spawn code, plain rock and fractured deposit the arrival radius.
  One left is timed; none is `foreign` or `out_of_radius`; several or a fifth is `ambiguous`;
- records a fractured deposit (`rock4_copper` → `rock4_copper_frac`) as a source, since the
  frac's first area is destroyed before any client holds its instance and its RPC reaches nobody;
- excludes as `stale` an `ItemDrop` whose `s_spawnTime` stamp is older than the window + 3 s:
  an old drop re-entering view is a first arrival but not new loot (synced game clock);
- exports per interval the four slowest timed matches, or every one over 1 s up to eight, as
  `loot_visibility_witness`.

Send legs decide delay against attribution: on a client, own destruction → own Instantiate and
Instantiate → first `ZDOData` send to the server (`loot_visibility_owner_*`); on the server,
first receipt → first send to each other peer (`loot_visibility_server_send_*`), read from the
peer's sent map after `ZDOMan.SendZDOs`. Both are bounded to 64 drops. If both stay near the
50 ms tick while the observer still reports >1 s, the tail is attribution.

Not covered: owner-side sources that destroy after dropping (tree, destructible) never match the
owner leg; a player's own inventory drop near a rock with the same item remains mis-timeable.

## Candidate, only after the split is measured

Force-send a newly created `ItemDrop` ZDO to nearby peers, reusing the native
`ZDOMan.ForceSendZDO` path that `OwnershipExpedite` already uses for owner grants
(`docs/ownership-expedite.md`). Targets the cadence, not the ordering; the ceiling is
therefore the network leg the probe measures, not the whole observed delay.

## Not to do

Claim the `MineRock5` ZDO on hit to drop locally: an `RPC_Damage` reaching a stale
owner is dropped silently (`MineRock5.cs:340`), so hits would be lost.
