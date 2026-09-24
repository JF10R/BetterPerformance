# Idle zone pre-generation

Optional, default off, dedicated server only. While no client is connected or joining, the
server generates the ungenerated zones just beyond the native ghost radius around recent
player activity, so that later exploration finds them already generated.

Why: on the dedicated server, every loop of 100 ms or more outside startup came from ghost
zone generation or RPC. In the 2026-09-23 session, all 15 intervals with a ghost `SpawnZone`
peak of 50 ms or more were dominated by the locations phase, 12 of them by `DungeonGenerate`
(peaks 348 ms and 308 ms). A zone generation is one synchronous transaction (global RNG,
physics penetration tests, ZDO save), so it cannot be sliced or moved to a worker. The only
way to keep it off the players' time is to run it when nobody is playing.

**Limit, stated plainly:** idle time exists only while the server runs with nobody on it. A
server started about 45 s before the first join and stopped about 5 min after the last leave
gives a few minutes per session. Starting the server earlier gives more.

## Mechanism

1. **Activity.** While peers are connected, each ready peer's reference-position zone is
   sampled every 5 s into a most-recent-first list of 64 distinct zones. The list is saved per
   world (`BepInEx/BetterPerformance/pregeneration/activity-<key>.txt`, key = hash of world
   name and seed) when the players leave, every 5 min while dirty, and on shutdown. A missing
   or corrupt file costs only the history.
2. **Idle.** `ZNet.GetPeers()` is empty. A peer enters that list on socket accept, before its
   handshake, so a joining client stops generation. Also required: the world is loaded, the
   locations are generated (`ZoneSystem.LocationsGenerated`, the vanilla gate), the start
   delay has passed, no world save is in progress (`ZNet.IsSaving()`).
3. **Plan.** At the start of each idle window: every zone within `total simulation distance +
   IdleRadiusZones` of an activity zone (vanilla ghost circle, radius + 0.8 zones), not yet
   generated, inside the 10,500 m world edge, ordered by the most recent activity zone that
   reaches it, then by distance. Capped by what remains of `MaxZonesPerRun`.
4. **Generate.** A postfix on `ZoneSystem.Update` makes at most one
   `SpawnZone(zone, SpawnMode.Ghost, out root)` call per frame, the call `CreateGhostZones`
   makes for a peer, after the same `IsZoneGenerated` test. A `false` return (terrain or
   location prefab still loading, which the call itself queued) is retried on later frames,
   abandoned after 300 attempts. Nothing runs on a frame where another zone was generated,
   a save is in progress, or more than `MaxMillisecondsPerFrame` had already passed (measured
   from a stamp at the head of the player loop).
5. **Stop.** The frame a peer appears, the window ends; a call in progress is atomic and
   finishes. One log line per window: zones generated, time, attempts, candidates left.
   Since 0.4.16, plain console lines (BepInEx `Message` level) mark the window for an operator
   waiting to connect: `Idle pre-generation started: N zones…`, then `…finished: N zones in S s`
   (or `nothing left to generate… Ready.`), or `…paused: a player is connecting`.

## Why the content is the same as vanilla's

Generation reads no clock, player or peer state. Vegetation seeds per prefab from world seed
and zone coordinates (`ZoneSystem.PlaceVegetation`); locations use `world seed + zone` and
their instances were placed at world creation (`ZoneSystem.PlaceLocations`); dungeons seed
from world seed and position (`DungeonGenerator.GetSeed`). `RandomSpawn` and `RandomObject`
read only RNG, biome, lava and elevation. The physics queries see the zone's own terrain and
the objects this call placed: a dedicated server instantiates saved objects only around its
own reference position (`ZNetScene.CreateDestroyObjects`), and one call per frame means no
other ghost zone's objects are alive, where vanilla can generate one per peer in the same tick.

Accepted differences, the same class vanilla already has, since it generates ghost zones
minutes to hours before anyone sees them:

- Time stamps written at object creation (`Beehive`, `Vine`, `ItemDrop`, `BaseAI` spawn
  times) are older.
- `Game.m_worldLevel` (a world modifier) scales initial ruin damage and some health values; it
  applies the value current at generation. Differs only if world modifiers change later.

Not accepted: which candidate site becomes a unique location (e.g. a trader) depends on
generation order, since placing one removes the others. Zones holding an unplaced unique
location are skipped and left to vanilla (`idle_pregen_skipped_unique`).

## Cost

Estimated from the 2026-09-23 server capture: about 40 ZDOs per ghost zone (median; quartiles
18 and 148; dungeon zones are the high end), about 37 bytes per ZDO in the saved world.
300 zones: about 12k to 45k ZDOs, 0.4 to 1.7 MB on disk, and `SavePrepare` (60 to 91 ms for
about 297k ZDOs) roughly 2 to 14 ms longer, if it scales with ZDO count. `MaxZonesPerRun` bounds it.

## Configuration

`[ServerGeneration]`, all require a restart:

| Key | Default | Meaning |
| --- | --- | --- |
| `IdlePregenerationEnabled` | `false` | Install on a dedicated server. |
| `IdleRadiusZones` | `2` | Rings beyond the native ghost radius. |
| `MaxZonesPerRun` | `300` | Most zones generated per server process. |
| `IdleStartDelaySeconds` | `10` | Delay after the world starts. |
| `MaxMillisecondsPerFrame` | `8` | Skip a frame that already spent this long. |

The module installs only when the game's `ZNet.IsDedicated()` compiles to the constant `true`
(the server build); a client or listen server gets `idle_pregen_status=not_dedicated`.

## Gauges

`idle_pregen_zones_generated`, `_attempts`, `_not_ready`, `_abandoned`, `_skipped_unique`,
`_busy_frames`, `_frame_clock_missing`, `_paused_by_peer`, `_peak_ms` per interval;
`_candidates_remaining`, `_activity_zones`, `_run_total` as levels; labels `idle_pregen_status`
and `idle_pregen_store`. Idle calls also appear in `zone_generation_ghost_*`, in intervals
with `peer_count` 0.
