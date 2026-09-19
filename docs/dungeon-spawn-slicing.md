# Dungeon spawn slicing

One opt-in client module, default off. It spreads the dungeon room spawn across frames
instead of placing every room in a single frame. It changes no layout, no ZDO and no
network state: it replays the calls the native loop already makes, in the same order.
Mechanism and evidence: dungeon spawn research.

## Mechanism

A dungeon interior is its own ZDO. `DungeonGenerator.Awake` calls `Load`, which reads the
room hash, position and rotation of each room into `m_loadedRooms`, then
`LoadRoomPrefabsAsync` registers the generator's ZDO through `ZoneSystem.SetLoadingInZone`
and issues one asynchronous prefab load per room. When the last prefab resolves,
`OnRoomLoaded` calls `Spawn`, which walks `m_loadedRooms` calling
`PlaceRoom(roomData, position, rotation, null, SpawnMode.Client)` for every room, then one
`SnapToGround.SnappAll`, then clears `m_loadedRooms`. That loop is the whole cost of a
crypt appearing: measured at 109 calls, mean 24.3 ms, max 45.0 ms.

The module puts a Harmony prefix on `Spawn`. When it applies, the prefix skips the native
loop and queues the generator on a shared coroutine that calls the same private `PlaceRoom`
for each entry, in the same order, until a per-frame allowance is spent. On the slice that
consumes the last room it performs exactly the native tail: `SnapToGround.SnappAll`, clear
`m_loadedRooms`, then `ReleaseHeldReferences`.

The prefix runs the native loop whole, and counts it, in four cases: the module is off, the
process is a server or has no `ZNet` instance, the local player is inside the dungeon
bounds, or the generator has no loaded rooms. Any exception in the prefix also falls back.
A fifth exit is not a fallback: a generator whose slice is already queued returns false so
the native loop does not place rooms a slice already owns.

`Spawn` hardcodes `SpawnMode.Client` and is called only from `OnRoomLoaded`. The layout
path, `Generate(int, SpawnMode)` with `Save`, never calls it, so `SpawnMode.Full` and
`Ghost` placement is outside the patched seam entirely. That is the gate: not a mode test in
the module, but the fact that the patched method has only one mode and one caller.

## Hazards and how each is handled

**The release that follows `Spawn` immediately.** `OnRoomLoaded` calls
`ReleaseHeldReferences` on the line after `Spawn` returns. That method releases every room
prefab reference and drops the loading-in-zone registration. Running it between slices would
leave later slices reading a released asset. A second prefix on `ReleaseHeldReferences`
returns false while that generator has a slice in flight, counting each deferral; the
coroutine calls it once, after the final slice, when the entry is no longer pending.

**Zone readiness.** `LoadRoomPrefabsAsync` is the only generator method that takes the
`SetLoadingInZone` registration and `ReleaseHeldReferences` is the only one that drops it,
both proved by IL. Deferring the release therefore holds the registration to the last slice
with no extra bookkeeping. `ZoneSystem.IsZoneLoaded` and `ZNetScene.IsAreaReady` stay false
exactly as they already do during asynchronous room loading, so teleport and respawn arrival
wait the same way.

**A player inside a half-built interior.** Client rooms are static geometry with colliders
and no ZDO, so a player standing inside while rooms are missing can fall through. When the
local player is inside the dungeon bounds on entry to `Spawn`, the native loop runs whole.
`Generate` assigns `m_zoneCenter` and never runs on a client, so the bounds are rebuilt from
the generator's transform with the zone formula `Generate` uses — minus one input the client
never has: `m_originalPosition` is assigned only on the peer that spawned the location
(`ZoneSystem.SpawnLocation`, Full/Ghost modes) and is not in the ZDO, so on a client the exact
y-centre is unknown. The check therefore centres y on the generator itself, doubles the
vertical extent, and also treats any player within `m_zoneSize.magnitude` of the generator as
inside. Every uncertainty widens "inside"; its only consequence is the native spawn. Until
0.4.10 the check used the zero field as if it were the real offset, which could report a
player standing inside as outside.

**A generator destroyed mid-slice.** A prefix on `OnDestroy` drops the entry and counts it
before the native `OnDestroy` reaches `ReleaseHeldReferences`, so that release is never
deferred for an object that is going away. The coroutine also drops an entry whose generator
has become null.

**Two dungeons resolving on the same frame.** One shared queue and one shared allowance. The
coroutine places rooms from the head entry, moving to the next when one finishes, until the
allowance is spent. Two crypts therefore share the frame rather than doubling it.

**A private signature change on a game update.** Every member is resolved through
`AccessTools` at install and the delegates are built then. An unsupported layout reports
`unavailable` and leaves the native single-frame spawn in place.

## Invariants

- Rooms are placed in the order `m_loadedRooms` holds, which is the order `Spawn` uses.
- `PlaceRoom` saves and restores `UnityEngine.Random.state` around its own work and derives
  its seed from the room position, so a frame boundary between two calls changes nothing.
- The client branch of `PlaceRoom` never touches `m_placedRooms`, open connections or
  `Save`, so no cross-room state is carried across a frame boundary.
- No ZDO is written. Other players and the server are unaffected.
- At least one room is placed per frame, so progress is guaranteed even if a single room
  costs more than the whole allowance.
- The runtime toggle stops new dungeons only. A slice already in flight always finishes.
- The coroutine host is one hidden `DontDestroyOnLoad` object carrying no state.

## Configuration

| Key | Default | Range |
| --- | --- | --- |
| `[Dungeons] SliceRoomSpawnEnabled` | `false` | |
| `[Dungeons] RoomSpawnBudgetMilliseconds` | `4` | 1 to 50 |

Requires a restart to install. The console command `bp_dungeon on | off | status` flips the
module in the running process, so one session can A/B the crypt approach; it does not write
configuration and does not reach another process.

## Telemetry

| Gauge | Unit | Meaning |
| --- | --- | --- |
| `dungeon_spawn_sliced` | calls | spawns the module took over |
| `dungeon_spawn_vanilla_player_inside` | calls | break-glass: player inside the bounds |
| `dungeon_spawn_vanilla_server_or_full` | calls | server or no network instance |
| `dungeon_rooms_placed` | rooms | rooms the coroutine placed |
| `dungeon_slices` | frames | frames that placed at least one room |
| `dungeon_slice_ms_max` | ms | longest single frame spent placing |
| `dungeon_spawn_span_frames_max` | frames | longest spawn, queue to final slice |
| `dungeon_spawn_aborted_destroyed` | calls | generators destroyed mid-slice |
| `dungeon_release_held` | calls | deferred `ReleaseHeldReferences` calls |
| `dungeon_probe_failures` | calls | exceptions, each one a fallback |

Labels `dungeon_slicing_status` (`disabled` | `installed` | `unavailable` | `patch_failed`),
`dungeon_slicing_enabled` and `dungeon_slicing_budget_ms`. All gauges drain per interval and
export whether or not the module installed.

## Verification

59 static game-contract checks read `DungeonGenerator` from assembly metadata, because the
type cannot be loaded outside Unity. They cover the private signatures of `Spawn`,
`PlaceRoom`, `OnRoomLoaded`, `ReleaseHeldReferences` and `OnDestroy`; the `m_loadedRooms`
array and the three fields of its element type; that `Spawn` iterates that array, calls
`PlaceRoom` once with a null connection and the client mode constant, then ends with
`SnappAll` and clears the array; that `OnRoomLoaded` calls `Spawn` before
`ReleaseHeldReferences`; that `LoadRoomPrefabsAsync` and `ReleaseHeldReferences` are the only
generator methods that take and drop the loading-in-zone registration; that no `Generate`
overload calls `Spawn` and `OnRoomLoaded` is its only caller; that `PlaceRoom` still saves
and restores the random state; that the bounds fields keep their shape; that the two
skipping prefixes return bool and the destroy escape returns void; and the configuration
defaults, range, and the full gauge and label set.

## What stays unproven

- The runtime look. Whether rooms appearing over several frames is visible on approach, and
  whether the deferred `SnappAll` leaves rooms briefly unsnapped, needs a disposable-world
  A/B with `bp_dungeon` in one session on a forest crypt.
- The gain. The expected result is the 24 ms mean becoming roughly `rooms / slice` frames of
  the configured allowance, but no before-and-after capture has been taken.
- Interaction with the object creation budget. The two allowances are independent and can
  land in the same frame, because this seam is an asset-load callback outside any
  `ZNetScene.CreateObjects` batch. Their sum is bounded only by the two configured values.
- Host-and-play. The module excludes any process where `ZNet.IsServer()` is true, so a
  player hosting their own world never slices, even though their client runs the same loop.
- Whether the bounds check needs a margin. A player just outside the bounds when `Spawn` is
  entered can walk in during the slice.
- Cost of the module itself. Three reflected field reads per room against a room that costs
  milliseconds, but that ratio has not been measured.
