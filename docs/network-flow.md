# Adaptive network flow

`Network.AdaptiveFlowEnabled` (default `false`, restart required). Both roles.

## What it replaces

Vanilla gates every peer's ZDO batch on a fixed 10,240-byte send queue: `ZDOMan.SendZDOs` returns
without sending when the socket holds more, otherwise sends at most `10240 - queue` bytes and
refuses anything under 2,048. It also pins Steam's send rate to 153,600 B/s globally, for a LAN
link and a relayed intercontinental link alike. BetterNetworking replaced both with menu constants
(Queue Size, Send Rate); this module replaces both knobs instead. A constant cannot fit two links:
on a 1 ms LAN link 10 KB is one tick of headroom, so one loss burst fills the queue and the next
send tick is skipped. That backed a second player's link up to 27 KB unacked while loot ZDOs
waited 1-4 s, measured 2026-09-18 on LAN Wi-Fi at 27 ms.

## Mechanism

`window = rate x (ping + 50 ms) x 1.5`, clamped to [16 KB, 256 KB]. `rate` is Steam's
`m_nSendRateBytesPerSecond` and `ping` its `m_nPing`, read once per peer per 250 ms; the 50 ms term
is the send tick, so the window covers one round trip plus one tick of production.

- 1 MB/s at 1 ms: `1048576 x 0.051 x 1.5` = 80,216 B, about 78 KB.
- 250 KB/s at 40 ms: `256000 x 0.09 x 1.5` = 34,560 B, about 34 KB.

Back-off: pending bytes over half the window on two updates at least 200 ms apart halve it and hold
growth for 2 s. Recovery is steps of at most +25 % per update, never a jump, so a link that just
recovered is not handed a burst it has not proven it can absorb. A peer with no rate estimate gets
the 32 KB default; at most 64 peers are tracked and the rest are served the default and counted.
Link class is evaluated when a peer is first seen and every 30 s, applied only on a change:

| Class | Test | `SendRateMin` | `SendRateMax` |
| --- | --- | --- | --- |
| LAN | ping <= 5 ms and not relayed | 1,048,576 | 1,048,576 |
| WAN | anything else | 153,600 (vanilla) | 1,048,576 |

A connection Steam will not describe counts as relayed, so the LAN ceiling is never applied on a
guess. Once per process the global `SendBufferSize` is raised to 512 KB, as BetterNetworking did.

Cost: one `SteamNetConnectionRealTimeStatus_t` read per peer per 250 ms; the two rewritten sites
cost one field read, one dictionary lookup and one compare per `SendZDOs` call. Nothing allocates
after a peer's first window; the rate policy boxes one `int` per applied change.

## Telemetry

| Gauge | What it decides |
| --- | --- |
| `net_flow_window_min_bytes`, `net_flow_window_max_bytes` | Whether the window moved at all. Both at 32,768 means no peer was measured. |
| `net_flow_updates` | Measured samples; zero with peers connected means the Steam read is failing. |
| `net_flow_backoffs` | Sustained backlog. Rising means the window is still over the link's capacity. |
| `net_flow_recoveries` | Back-offs that reached target again. Far below `backoffs` means the link never recovers. |
| `net_flow_unknown_rate` | Updates served the default because Steam had no rate estimate. |
| `net_flow_peers`, `net_flow_peers_over_capacity` | Peers tracked, and updates refused past the 64-peer bound. |
| `net_flow_lan_links`, `net_flow_wan_links` | How each peer was classed. A LAN player counted WAN means the relay test or the threshold is wrong. |
| `net_flow_rate_sets` | Applied class changes. Climbing every interval means a link flaps across the threshold. |
| `net_flow_failures` | Failed reads or config writes. Eight disable the module. |

Labels: `net_flow_status` (`disabled`, `yield_to_betternetworking`, `foreign_patch:<owner>`,
`unavailable_unexpected_shape`, `installed_disabled`, `enabled`, `failed`), `net_flow_enabled`,
`net_flow_scope`.

## Contract check

`NetworkFlowGameTests` asserts the exact vanilla shape the rewrite depends on (two 10,240
constants, one 2,048, one send-queue read), rejects each single-site mutation of that body, pins
the Steamworks signatures, status fields and config enum values, and prints a NOTE if the game
stops pinning the send rate globally, which would make the rate half redundant.

## Coexistence and what not to do

With BetterNetworking loaded the module yields entirely: no patch, status
`yield_to_betternetworking`. A foreign Harmony owner on `ZDOMan.SendZDOs` or
`ZSteamSocket.GetSendQueueSize` yields the same way. Both ends do not need the module: each side
shapes only what it sends. Do not make the window a config value; the point is that the right
value is not a constant. Do not class a link LAN from ping alone, since a relayed link can read a
low ping. Do not read the Steam status per `SendZDOs` call: that call is on the send path.
