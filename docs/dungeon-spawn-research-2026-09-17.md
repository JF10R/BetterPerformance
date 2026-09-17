# Dungeon and location spawn slicing on the client (research, 2026-09-17)

Question: why does a dungeon or location spawn cost ~24 ms (up to 48 ms) in one client frame while
exploring forest crypts, and can an opt-in module slice it across frames without changing the
generated layout, the network state, or when the player may enter?

Scope: client only. Read-only study of game method and field names, plus this plugin's modules.
Verdict first: **feasible, bounded, medium risk**. The client room loop is the right seam.

## 1. Verified client path (read from the game code)

- `ZNetScene.CreateObject` creates the `LocationProxy` ZDO instance. `LocationProxy.Awake` calls
  `SpawnLocation`, which reads `s_location` and `s_seed` from its own ZDO. If
  `ZoneSystem.ShouldDelayProxyLocationSpawning` says the location prefab is not resident, the proxy
  registers itself with `ZoneSystem.SetLoadingInZone` and retries from `Update` until it is.
- `ZoneSystem.SpawnProxyLocation` calls the private `ZoneSystem.SpawnLocation` with
  `SpawnMode.Client`. In that branch the method does **not** instantiate per-object `ZNetView`
  prefabs. It randomizes `RandomSpawn` and `RandomObject` from the seed, deactivates every
  `ZNetView` child inside the shared prefab asset, performs **one** `Instantiate` of the whole
  location prefab, then reactivates and restores the asset. So the client gets the static shell plus
  decorations in a single Unity instantiate; networked objects arrive separately as ZDOs.
  This is the `LocationSpawn` probe: 476 calls, mean 4.8 ms, max 48.3 ms.
- The dungeon interior is a separate ZDO. Its `DungeonGenerator.Awake` calls `Load`, which reads the
  `s_roomData` byte array from the ZDO (room hash, position, rotation per room) into `m_loadedRooms`.
  It then calls `LoadRoomPrefabsAsync`, which registers the generator's ZDO through
  `SetLoadingInZone` and issues one async load per room prefab.
- When the last room prefab resolves, `OnRoomLoaded` calls `Spawn`, which loops **all** rooms in one
  frame: `PlaceRoom(roomData, position, rotation, null, SpawnMode.Client)` per entry, then one
  `SnapToGround.SnappAll`, then `ReleaseHeldReferences`. This is the `DungeonSpawn` probe:
  109 calls, mean 24.3 ms, max 45.0 ms. **The 24 ms is this loop, not generation.**
- Per room, the client branch of `PlaceRoom` does: three `GetEnabledComponentsInChildren` walks over
  the room prefab asset, `Prepare` and `Randomize` on each `RandomSpawn` and `RandomObject`,
  `SetActive(false)` over every `ZNetView` child of the asset, **one** `Instantiate` of the room
  prefab parented to the generator, then `Reset` and `SetActive(true)` to restore the asset.
- `DungeonGenerator.Generate` (layout, collision tests, `Save` into the ZDO) runs only in
  `SpawnMode.Full` and `Ghost`. On the client it never runs. The server figure `DungeonGenerate`
  3 calls mean 49 ms is that other path and is out of scope here.
- Readiness: `ZoneSystem.IsZoneLoaded` returns false while any ZDO is registered through
  `SetLoadingInZone`, and `ZNetScene.IsAreaReady` returns false when the zone is not loaded. The
  generator already holds that registration across an arbitrary number of frames during async room
  prefab loading, and releases it in `ReleaseHeldReferences` after `Spawn`.

Assumed, not verified: that the per-room `Instantiate` dominates the 24 ms rather than the three
hierarchy walks and the activate/deactivate toggles. Telemetry in section 4 settles it.

## 2. Can the synchronous part be split

Yes for the dungeon room loop. Each iteration of `Spawn` is independent:

- Room placement data comes entirely from the ZDO, so iteration order does not affect positions.
- The per-room random seed is derived from the room's own position (plus the dungeon seed when
  `m_addBaseSeedToRandomSpawn` is set), not from a running sequence. `PlaceRoom` saves and restores
  `UnityEngine.Random.state` around its own work.
- In client mode `PlaceRoom` never touches `m_placedRooms`, `m_openConnections`, connection walking
  or `Save`. No cross-room state is carried.
- Every mutation of the shared prefab asset (deactivate, randomize, reset, reactivate) is balanced
  inside one `PlaceRoom` call, so a frame boundary between two calls leaves no dangling asset state.
- No ZDO is written by the client path, so the network state and other players are unaffected.

Invariants to preserve while slicing:
1. Keep the generator's ZDO registered through `SetLoadingInZone` until the last slice. That is the
   existing readiness contract and it keeps `IsZoneLoaded` and `IsAreaReady` false, so teleport and
   respawn arrival still wait exactly as they do during async room loading today.
2. Do not let `ReleaseHeldReferences` run between slices. `OnRoomLoaded` calls it immediately after
   `Spawn` returns; releasing the room prefab references early would leave later slices reloading or
   reading a null asset.
3. Keep `m_loadedRooms` alive until the last slice (vanilla nulls it at the end of `Spawn`).
4. The client rooms are plain static geometry with colliders and no ZDO. A player standing inside
   the dungeon bounds while rooms are missing can fall through. Slicing must not apply in that case.

The whole-location `Instantiate` inside `ZoneSystem.SpawnLocation` is a **single** Unity call and
cannot be split without reimplementing the prefab instantiate. The 48 ms max there is one prefab, not
a loop. Leave it alone.

`ObjectCreationBudget` cannot cover either cost. Its gate only decides whether to start the next
object in a `ZNetScene.CreateObjects` batch; one object that takes 48 ms still runs whole. And
`DungeonGenerator.Spawn` is driven by an asset-load callback, entirely outside any creation batch.

## 3. Candidate designs

**A. Sliced room spawn (recommended).** Prefix `DungeonGenerator.Spawn` (no arguments), return false
to skip the original, and drive a per-instance coroutine that calls the private
`PlaceRoom(DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, SpawnMode)` for each entry of
`m_loadedRooms` until a soft per-frame allowance is spent, then yields. On the final slice: call
`SnapToGround.SnappAll`, null `m_loadedRooms`, then release. Pair it with a prefix on
`ReleaseHeldReferences` that returns false while that instance has a slice in flight, with an escape
for `OnDestroy` and a self-call at the end. Break glass: if the local player is inside the dungeon
bounds (`m_zoneCenter`, `m_zoneSize`) when `Spawn` is entered, run the vanilla loop whole.
Risks: a private-method signature change on a game update (guard with `AccessTools.DeclaredMethod`
and fall back to vanilla); a coroutine host destroyed mid-slice (check `this` and `gameObject` each
iteration, as `OnRoomLoaded` already does); another mod patching `Spawn`.

**B. Shared admission queue.** Same seam, but instead of one coroutine per generator, enqueue
pending dungeons in a single scheduler that admits N rooms per frame across all of them. This is the
only design that helps when two crypts resolve their prefabs on the same frame, which the 109 calls
over a handful of crypts make likely. More code, same invariants.

**C. Defer `SnapToGround.SnappAll` only.** Cheapest patch, smallest gain. `SnappAll` first calls
`Heightmap.ForceGenerateAll` and then drains a global snapper list, so its cost is not per room.
Worth measuring before building A, and A must decide whether to call it per slice (earlier visual
correctness, repeated `ForceGenerateAll`) or once at the end (vanilla-identical, rooms may sit
unsnapped for a few frames).

Interaction: A and B are outside `ObjectCreationBudget`'s batch, so the two allowances do not
compose. They can land in the same frame, and the sum should be bounded by config rather than by
hoping they do not coincide. `InitialLoadingOptimization` only accelerates the first client spawn
through `ZoneSystem.Update`; it does not touch this path, but a sliced dungeon during initial join
would hold `IsZoneLoaded` false a little longer, which is the same effect async room loading already
has.

## 4. Count-only telemetry to attribute the cost

Before changing anything, add to the existing simulation probes, capture-gated and count only:

- rooms per `DungeonSpawn` call (length of `m_loadedRooms`), sum, max, and a histogram bucket.
- per-room elapsed inside the client branch of `PlaceRoom`, to confirm cost is linear in rooms.
- objects instantiated per room: child count of the instantiated room, and the counts returned by
  the three `GetEnabledComponentsInChildren` walks, to separate walk cost from instantiate cost.
- `SnapToGround.SnappAll` elapsed and snapper count at the end of `Spawn`.
- location side: rooms are irrelevant, so record `ZNetView`, `RandomObject` and `RandomSpawn` counts
  per `LocationSpawn` call to explain the 48 ms outlier.
- repeat rate: `DungeonSpawn` calls per distinct generator ZDO, which tests whether the 109 calls are
  re-entries as the player crosses the zone edge rather than distinct crypts.

## 5. Verdict

Feasible. Expected gain: the 24 ms mean and 45 ms max of `DungeonSpawn` become roughly
`rooms / slice_size` frames of a configurable allowance, so a 4 ms target should remove the visible
hitch on crypt approach without changing layout or network state. No gain on `LocationSpawn`, whose
cost is one indivisible prefab instantiate. Risk is medium and concentrated in two places: holding
`ReleaseHeldReferences` back correctly, and the player walking into a half-built interior, which the
bounds check and the existing loading-in-zone registration both address. Measure section 4 first.

Implemented as an opt-in module: [dungeon spawn slicing](dungeon-spawn-slicing.md).
