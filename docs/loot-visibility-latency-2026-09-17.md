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

The 2026-09-16 session recorded all 367 `RPC_Damage` on `rock4_copper_frac` handled by the
host client, so those drops were instantiated locally and no network leg existed
(`session-report-2026-09-16.md:26`). Ownership decides which of the two cases above the
player is in, and it varies. The probe reports both and says which one happened:
a recorded arrival means the rock was owned elsewhere, `arrival_missing` means this
process dropped the ore itself.

## Probe, implemented

`LootVisibilityTelemetry` plus `LootVisibilityTracker` in Core, exporting
`loot_visibility_*`. Client side only, one clock — cross-process Stopwatch origins are
offset on this runtime (`tasks/lessons.md`). Three postfixes: `MineRock5.RPC_SetAreaHealth`
(t0, on every client whether or not it owns the rock), the private
`ZDOMan.CreateNewZDO(ZDOID, Vector3, int)` whose zero prefab hash is what distinguishes a
network arrival from local creation (t1), and `ZNetScene.CreateObject` (t2).

Attribution is positional: a destroyed rock is joined to a drop by distance and time, not
by identity, because the game records no link between them. t0 is the rock root rather
than the hit-area centre, which is why the radius cannot be small. Every hook returns
after one bounded check when no destruction is pending, so the probe costs nothing
outside a few seconds after each mined chunk.

Defaults: `LootVisibilityEnabled` true, radius 12 m, window 5 s.

## Candidate, only after the split is measured

Force-send a newly created `ItemDrop` ZDO to nearby peers, reusing the native
`ZDOMan.ForceSendZDO` path that `OwnershipExpedite` already uses for owner grants
(`docs/ownership-expedite.md`). Targets the cadence, not the ordering; the ceiling is
therefore the network leg the probe measures, not the whole observed delay.

## Not to do

Claim the `MineRock5` ZDO on hit to drop locally: an `RPC_Damage` reaching a stale
owner is dropped silently (`MineRock5.cs:340`), so hits would be lost.
