# Host and network path telemetry

Read-only host facts, Steam transport path classification, and ownership/replication call
counts. Nothing here changes priority, affinity, the power scheme or the timer resolution;
no address, identity or player name is exported.

## Network path (`SteamTelemetry`)

| Name | Kind | Meaning |
| --- | --- | --- |
| `steam_connections_relayed` | gauge | Peers whose `m_nFlags` carries the Relayed bit (16) or that report a relay POP. |
| `steam_connections_direct` | gauge | Peers described by Steam with neither of those. |
| `steam_connections_info_unavailable` | gauge | `GetConnectionInfo` returned false for an otherwise sampled peer. |
| `peer_sockets_steam` / `_playfab` / `_other` | gauge | Peer sockets by runtime type, after unwrapping the ServerSync wrapper. |
| `steam_transport_path` | label | `direct`, `relayed`, `mixed`, or `unknown` when no peer was described. |
| `steam_relay_pop` | label | Valve datacenter code decoded from the packed POP id, else `none`, `multiple`, `invalid`. |
| `online_backend` | label | `ZNet.m_onlineBackend`: Steamworks, PlayFab, EOS, CustomSocket, None. |
| `steam_transport_scope` | label | `flags_from_GetConnectionInfo; relayed_LAN_link_adds_latency; no_addresses_exported` |

A client and a dedicated server on one machine should read `direct` with `steam_relay_pop=none`.
A `relayed` path there means traffic leaves through a Valve relay and comes back, adding
latency no in-process timing will explain. `online_backend=PlayFab` means the crossplay path,
so the Steam gauges stay empty while `peer_sockets_playfab` is not.

Limits: the flags describe the transport Steam selected, not a measured route. `m_addrRemote`,
`m_identityRemote` and the connection description are never read; `unknown` before any peer
connects is normal.

## Host facts (`HostTelemetry`)

Start labels: `process_priority_class`, `process_affinity_mask` (hex), `power_scheme`,
`os_version`, `host_status`, `host_scope`. Start gauge: `host_logical_processors`.

| Name (each poll) | Meaning |
| --- | --- |
| `host_timer_resolution_current_ms` | Active system timer period; it changes while the game runs. |
| `host_timer_resolution_min_ms` / `_max_ms` | Finest and coarsest periods the kernel supports. |
| `host_qpc_timestamp` / `host_qpc_frequency` | Raw `QueryPerformanceCounter` value and ticks per second. |
| `host_timer_resolution_status`, `host_qpc_status` | `available`, `read_failed`, `native_api_unavailable`, `unsupported_platform`. |

A current resolution near 15.6 ms paces sleeps and waits coarsely, so a sub-frame wait can
cost a full tick; near 0.5 ms something on the host asked for a finer timer. The counter pair
is the intended alignment key between a client and a server capture, because `tasks/lessons.md`
records Stopwatch origins differing between the two Unity processes here. That alignment is
not yet validated against a known-simultaneous event; treat a cross-capture join as a
hypothesis until it is.

Limits: Windows only; elsewhere the labels read `unsupported_platform` and no gauge is
emitted. Priority, affinity and the power scheme are read once at capture start.

## Ownership and replication (`OwnershipTelemetry`)

Per-interval counts drained at each poll: `zdo_set_owner_calls`, `zdo_request_rpcs`,
`item_request_own_rpcs`, `container_open_requests`. Health: `ownership_other_thread_skips`,
`ownership_probe_failures`, labels `ownership_telemetry_status` and `ownership_scope`.

From `ZDOMan.instance`, guarded by `zdoman_counters_status`: `zdoman_zdos_sent_total`,
`zdoman_zdos_recv_total`, their `_delta` counterparts (omitted on the first poll, clamped at
zero after a counter reset), `zdoman_zdos_sent_last_sec`, `zdoman_zdos_recv_last_sec`,
`zdoman_client_change_queue`, `zdoman_dead_zdos`.

A rising `zdo_request_rpcs` with a growing `zdoman_client_change_queue` points at replication
pressure rather than local CPU. The item and container gauges count outgoing requests only.

Limits: counts of native calls, not latency; request completion latency is in the action
telemetry. Hooks count only on the installing thread, anything else lands in
`ownership_other_thread_skips`.
