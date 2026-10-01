# Position jump sync

`PositionJumpSync` (0.4.19). Config `[Teleport]`.

## What vanilla does

- A client sends its reference position to the server every 2 s (`ZNet.SendPeriodicData` → `SendServerSyncPlayerData`, RPC `ServerSyncedPlayerData`).
- The server uses it to choose which objects to stream to that client (`ZDOMan.CreateSyncList` → `GetRefPos`), and to fill the player list it sends everyone on its own 2 s timer (`SendPlayerList`, RPC `PlayerList`).
- The map draws other players from that list (`Minimap.UpdatePlayerPins` → `GetOtherPublicPlayers`).

So after a portal the server can keep streaming the old area for up to 2 s, and the other players' maps show the old position for 0–4 s. Fast portal arrival (3–6 s instead of 8) made this visible on 2026-09-26.

## What the module does

- **Always (observation):** measures the delay from a jump of at least `PositionJumpMeters` (default 64 m) to the send that carries it (client), and from its receipt to the next player list (server).
- **`SendPositionOnJumpEnabled` (client, opt-in):** on such a jump, calls the native send at once. It carries the same message and the same value vanilla would send within 2 s.
- **`RelayPlayerListOnJumpEnabled` (dedicated server, opt-in):** when a received position jumped, calls the native `SendPlayerList` at once.

Both are rate-limited to one early send per 0.25 s. The first position after a join (from the origin) is not a jump.

This is not the rejected "early reference move": the reference position is still the one the game sets after the player has moved. Only the send delay changes.

## Gauges

| Gauge | Role | Reads as |
|---|---|---|
| `position_jump_detected`, `position_jump_distance_max_m` | client | jumps seen |
| `position_jump_send_delay_ms_max` / `_sum`, `position_jump_send_delays` | client | jump → send; ~0 with the switch on, 0–2000 ms off |
| `position_jump_early_sends` | client | sends the switch added |
| `position_sync_sends` | client | all sends; ~1 per 2 s plus early ones (cadence unchanged) |
| `position_jump_received` | server | jumps received |
| `player_list_jump_delay_ms_max` / `_sum`, `player_list_jump_delays` | server | receipt → list; ~0 on, 0–2000 ms off |
| `player_list_sends`, `player_list_early_sends` | server | all lists / lists the switch added |
| `position_jump_failures` | both | must stay 0 |

Labels: `position_jump_sync_status`, `position_jump_send_enabled`, `position_jump_relay_enabled`.

## Limits

- The map icon lag seen by another player is the client delay plus the server delay plus the network, then the map's own glide (below).
- Mixed installs are fine: an early send is an ordinary vanilla message, so a server without the module simply receives it sooner.

## Map pins (0.4.20)

`Minimap.UpdatePlayerPins` moves another player's pin toward the listed position at 200 m/s (`Vector3.MoveTowards`), so a pin needs 8 s after a 1.6 km portal and 20 s after 4 km, whatever the network did. `MinimapPlayerPinSnap`, `[Map] SnapPlayerPinsOnJumpEnabled` (client, opt-in), places the pin at once when it is farther than `PlayerPinJumpMeters` (default 64 m) from the listed position; walking keeps the native glide. Display only. Gauges `map_pin_snaps`, `map_pin_snap_distance_max_m`, `map_pin_snap_failures`; label `map_pin_snap_status`.
