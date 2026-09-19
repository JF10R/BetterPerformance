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

## Link figures (0.4.11)

### Why the server read zero

`ZNet.GetNetStats` aggregates `ZSteamSocket.GetConnectionQuality` over ready peers. On the
dedicated server build that method still asks the client interface
`SteamNetworkingSockets.GetConnectionRealTimeStatus`, which has no game-server context there and
returns a non-OK result, so every figure collapses to zero. Confirmed on the installed 1.0.15
assemblies: the server build moved `GetSendQueueSize` and `GetCurrentSendRate` to
`SteamGameServerNetworkingSockets` and left `GetConnectionQuality` behind. A 4.5 h capture with
two peers connected recorded every `host_net_*` gauge at zero on the server.

The plugin therefore reads each ready peer's connection itself, through
`SteamGameServerNetworkingSockets` when `ZNet.IsDedicated()` and the client interface otherwise,
a listen-server host being a client context. The `HostNet` game test prints a NOTE naming the
interface `GetConnectionQuality` calls on the assembly under test, so an upstream fix shows up
the next time the harness runs against either build.

### On a server

| Name | Meaning |
| --- | --- |
| `host_net_ping_ms` | Mean round trip over measured peers, as the game averages it. |
| `host_net_ping_max_ms` | Worst peer, which is the remote player when one is farther away. |
| `host_net_quality_local` / `_remote` | Minimum over peers, not the mean: the worst link decides. |
| `host_net_out_bytes_per_sec` / `_in_bytes_per_sec` | Sums over measured peers. |
| `host_net_pending_bytes_max` | Largest per-peer total of pending reliable, pending unreliable and sent-unacked reliable bytes. |
| `host_net_peers_ready` / `_measured` / `_unmeasured` | Ready peers, and how many Steam described. |

`host_net_status` reads `server_per_peer` when at least one peer was measured,
`server_no_peers` when none is ready, `server_peers_unmeasured` when peers are ready and Steam
described none. The value gauges are omitted rather than exported as zero in the last two cases.
A `native_fallback:<reason>` means a field or Steam signature the sampler needs has changed and
the native `GetNetStats` figures are being exported instead; `unavailable:<ExceptionTypeName>`
means the interval threw and exported nothing.

### On a client

`host_net_status` stays `client_server_link` and the five native `GetNetStats` figures keep
their meaning, because the client interface is the right one there.
`host_net_pending_bytes_max` is added for the server link, so the client's own upload backlog is
visible on the same scale as the server's.

### What the pending-bytes gauge decides

`ZDOMan.SendZDOs` reads `ISocket.GetSendQueueSize` for a peer and skips that peer's sync tick
when the queue is over 10240 bytes, or when fewer than 2048 bytes remain under it. Pending
reliable, pending unreliable and sent-unacked reliable are the three counters that total, so
`host_net_pending_bytes_max` near 10240 means ZDO updates for that peer are being held back.
BetterNetworking's Queue Size setting raises the effective cap to 32 KB, so read the gauge
against whichever cap the installation runs.

`host_net_ping_max_ms` and the two minimum qualities identify the remote player's link. Compare
them with the loot-visibility network leg, measured at 1.5 s mean and 4.6 s maximum on the local
client when the other player owned the destroyed object: a leg far above the ping is queueing or
ownership latency, not the link.

Limits: these are Steam's own estimates for the transport, sampled once per capture interval,
not measured by the plugin. A peer Steam does not describe is counted in
`host_net_peers_unmeasured`, never as a zero.
