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

### Which caller set the owner

`zdo_set_owner_calls` counts every `ZDO.SetOwner` and keeps that meaning. Seven counters split
it by the native method that was running at the time, set by a prefix/finalizer pair on each
caller and read by the `SetOwner` prefix. A finalizer, not a postfix, so a throwing native call
cannot leave a marker set.

| Counter | Caller |
| --- | --- |
| `zdo_set_owner_calls_release_to_zero` | a `ZDOMan.ReleaseNearbyZDOS` pass releasing to owner 0 |
| `zdo_set_owner_calls_release_claim_peer` | a peer pass claiming for that peer |
| `zdo_set_owner_calls_release_server_pass` | the server's own reference pass claiming for the session |
| `zdo_set_owner_calls_zdo_data_reapply` | `ZDOMan.RPC_ZDOData` |
| `zdo_set_owner_calls_disconnect_sweep` | `ZDOMan.RemovePeer` and the orphan sweep it runs |
| `zdo_set_owner_calls_invalid_prefab_destroy` | `ZNetScene.CreateObjectsSorted` and `CreateDistantObjects` |
| `zdo_set_owner_calls_other` | every other caller, including client gameplay code |

The three release counters are zero on a client: the release loop runs only on a server. A
marker whose game signature changed is named in `ownership_markers_unavailable`, its own split
counter then reads zero, and `ownership_caller_split_status` becomes `partial`. Counters that
lost their marker land in `zdo_set_owner_calls_other`, so the seven still sum to the parent.

### Release cycles

One cycle is one `ZDOMan.ReleaseZDOS` call that did work: the server pass followed by one pass
per peer, all inside a single `Update`. `release_cycles` counts them.

| Counter | Meaning |
| --- | --- |
| `release_cycle_released` | owner writes to 0 inside a cycle |
| `release_cycle_reclaimed` | ZDOs released by one pass and claimed again by a later pass of the same cycle |
| `release_cycle_net_changes` | ZDOs whose owner at cycle end differs from the owner at cycle start |
| `release_cycle_capacity_skips` | ZDOs a cycle touched beyond the 65536-entry tracking cap |

The decision these exist to settle: a net-change design that stages the cycle and applies only
final transitions is worth building only if `release_cycle_reclaimed` is a material fraction of
`release_cycle_released`. If `release_cycle_released` plus the two claim counters is already
close to `release_cycle_net_changes`, there is no churn to remove and
`zdo_set_owner_calls_other` names where the calls actually come from.

Limits: the cycle map holds the first owner seen and the last owner written per ZDO, both
maintained as the cycle runs, so closing a cycle reads nothing back from the game and a ZDO
destroyed mid-cycle is not re-resolved. A capacity skip is an untracked ZDO, not a ZDO that did
not change; when `release_cycle_capacity_skips` is above zero the other three cycle counters are
lower bounds.

Limits: counts of native calls, not latency; request completion latency is in the action
telemetry. Hooks count only on the installing thread, anything else lands in
`ownership_other_thread_skips`.

## Native link figures (0.4.10)

`host_net_ping_ms`, `host_net_out_bytes_per_sec`, `host_net_in_bytes_per_sec`,
`host_net_quality_local` and `host_net_quality_remote` come from `ZNet.GetNetStats`, the same
call behind the game's F2 overlay. `host_net_status` says what they describe:
`client_server_link` on a client, `server_peer_aggregate` on a server (quality and ping averaged,
bytes summed over ready peers), `no_world` before a session, `unavailable` if the call threw.
They are sampled once per capture interval and are Steam's own estimates, not measured by the
plugin. Read the loot-visibility network leg and the ownership grant waits against the ping:
neither can be shorter than half of it.
