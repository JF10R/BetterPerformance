# Owner-grant expedite (server side)

Optional, default off. Reorders one server send queue; changes nothing else.

## Mechanism

On the server, `ZDOMan.RPC_ZDOData` applies an incoming ZDO update and can change that
ZDO's owner. A pickup grant arrives this way and then waits for the ordinary sorted,
byte-budgeted queue to reach the new owner. The container path does not wait: its
`RPC_RequestOpen` calls `ForceSendZDO`, and `AddForceSendZdos` does `syncList.Insert(0, zdo)`.

This module gives an owner grant the same queue priority. A prefix/finalizer pair on
`RPC_ZDOData` marks the incoming scope and resolves the sending peer from its `ZRpc`; a
prefix/postfix pair on `ZDO.SetOwnerInternal` captures the previous owner and compares it
with the applied one. When the new owner is a connected peer that is not the sender, the
module calls the native `ZDOMan.ForceSendZDO(peerID, id)`.

Mechanism and candidate rationale: ownership-latency-research-2026-09-15.md.

## Invariants

- The owner is assigned by the native handler before the hook runs. The module never
  writes ownership, data, or a revision, and never destroys or reorders a save.
- Server only (`ZNet.IsServer()`), main thread only, and never to the sender.
- A peer that already holds the revision is not sent to: `AddForceSendZdos` still applies
  the native `ZDOPeer.ShouldSend` test and drops the entry when it fails.
- Forced inserts are bounded per second on a module-local clock, with a duplicate guard
  per peer and ZDO, so the bound holds whether or not a capture is recording.
- Install validates exact signatures and that `RPC_ZDOData` has exactly two
  `SetOwnerInternal` apply sites. Any other shape leaves native behaviour untouched.

## Configuration

| Key | Default |
| --- | --- |
| `[Ownership] ExpediteOwnerGrantsEnabled` | `false` |
| `[Ownership] ExpediteMaxPerSecond` | `256` |

Counters are installed and exported regardless of the key; only the forced insert is gated.

## Validating

Server side, now: run the server with the key off, then on, and compare
`ownership_grants_observed` against `ownership_grants_expedited` and the four skip
counters. `ownership_expedite_status` must read `enabled`, and `ownership_expedite_failures`
must stay 0. Watch `zdoman_objects_total` and `zdoman_zdos_sent_last_sec` for a load change.

The latency claim is not provable from the server alone. It needs the second player's
ownership wait distribution from her own action telemetry, once her plugin exists; compare
p95, not the mean, since the candidate targets the tail and not the floor.

## What not to do

Do not grant, release or predict ownership here, and do not relax `ItemDrop.RPC_RequestOwn`'s
`IsOwner()` guard. Do not report an effect from server counters alone: a rise in
`ownership_grants_expedited` proves the inserts happened, not that anything got faster.

## Unproven

The offline harness cannot patch `RPC_ZDOData` on .NET Framework, so signature, apply-site
and bound checks are static; the installed patch itself is verified only in the Unity runtime.
