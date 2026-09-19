# Sector invalidation after a position write

## Symptom

A player walks through a portal and every other player keeps seeing a frozen double of
them standing at the departure portal. Seen twice on 2026-09-18, at about 19:29 and again
around in-game Day 20, 07:33, in both travel directions. The double disappears only when
the teleported player happens to cross a sector boundary at the destination.

## Mechanism

`ZDO.InternalSetPosition` calls `SetSector` **before** it assigns `m_position`. When the
sector changes and the peer is the server, `SetSector` calls
`ZDOMan.ZDOSectorInvalidated`, which asks every `ZDOPeer` to drop the ZDO:
`ZDOPeer.ZDOSectorInvalidated` removes it from `m_zdos` and adds it to `m_invalidSector`
only when `ZNetScene.InActiveArea(zdo.GetPosition(), peer.GetRefPos())` is false. At that
moment `GetPosition()` still returns the **old** position, which is by definition inside
the observer's area, so nothing is invalidated. The server then never resends the ZDO
either, because it now lives in a sector the observer does not subscribe to. The
observer's `ZNetScene` keeps the stale instance. For an object that walks one sector at a
time the same bug only delays the invalidation by one crossing; for a teleport it never
resolves. The client side already works: an id arriving in the `RPC_ZDOData` invalid-sector
prefix runs `ZDO.InvalidateSector` and `ZNetScene.RemoveObjects` destroys the instance.

## Fix

`SectorInvalidationFix` adds a Harmony prefix and postfix to `ZDO.InternalSetPosition`.
The prefix records the old position and its sector index. The postfix, on the server only,
recomputes the sector index from the new position and, when it differs, calls
`ZDOMan.ZDOSectorInvalidated` a second time. `GetPosition()` now returns the new position,
so every peer whose active area no longer contains the object is told at once.

The second call is safe and idempotent. It is the same native call, it only ever adds ids
to `m_invalidSector` (a `HashSet`) and removes entries from `m_zdos`, and it re-tests
`InActiveArea` per peer, so a peer that still sees the object is untouched. A peer that
was already invalidated on the first call gets a no-op. The client needs no change: it
already handles the invalid-sector prefix correctly.

The postfix mirrors the native early-out for portal prefabs. `SetSector` returns before
touching anything for a ZDO whose prefab is in `Game.PortalPrefabHash`, so invalidating
one here would drop an object the server never re-adds to a sector.

## Cost

The prefix and postfix run on every server-side position write, thousands per second. The
common path is one position read, one `ZoneSystem.GetSectorIndex` (integer math) and one
`uint` compare per hook, with no allocation. The peer scan that measures
`sector_fix_invalidations_added` runs only on jumps over 64 m and is the only allocating
path apart from failure logging.

## Telemetry

| Gauge | What it decides |
| --- | --- |
| `sector_fix_sector_changes` | How often the hook does any work at all. A server-wide rate; if it is near zero the module is idle and its cost is the two hook calls. |
| `sector_fix_zone_jumps` | Sector changes with an XZ distance over 64 m, that is teleports and respawns. Counts the events the fix exists for. |
| `sector_fix_invalidations_added` | Ids the re-issued call added to peer invalid-sector sets on those jumps. Greater than zero on a jump is the proof the fix fired and that the native call had missed those peers. |
| `sector_fix_failures` | Hook exceptions this interval. After 8 total the module disables itself and the status label reads `failed`. |

Labels: `sector_fix_status`, `sector_fix_enabled`, `sector_fix_scope`.

## Contract check

`Install` refuses to patch unless the shape holds: `ZDO.InternalSetPosition` calls
`SetSector` before the `m_position` store, `SetSector` raises
`ZDOMan.ZDOSectorInvalidated`, the nested `ZDOPeer.ZDOSectorInvalidated` still reads
`ZDO.GetPosition` and `ZNetScene.InActiveArea`, and `ZDOMan.m_peers` and
`ZDOPeer.m_invalidSector` have the expected types. If the store comes first, the game has
fixed the bug: the status becomes `superseded_by_game`, nothing is patched, and this module
should be deleted. Any other mismatch yields `unavailable_unexpected_shape`.
`SectorInvalidationGameTests` asserts all of it against the installed game assembly.

## What not to do

Do not transpile `InternalSetPosition` to store the position before calling `SetSector`.
`SetSector` derives the old sector from `m_position` (`OutsideZones ? SectorZero :
GetSectorIndex()`), so moving the store makes the old and new sector identical and the
change detection, the sector bucket move and the invalidation all stop happening.
