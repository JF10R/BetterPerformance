# Capture relay

## What it does

A client mirrors its capture records to the server it plays on, as they are written, so the
server's `BepInEx/BetterPerformance/captures/remote/` holds the other players' captures beside
its own without anyone copying files. Optionally the client's `BepInEx/LogOutput.log` travels
the same way. Config section `[Relay]`, every key off by default, all need a restart:

| Key | Role | Meaning |
| --- | --- | --- |
| `SendCapturesEnabled` | client | Stream each capture record as the writer puts it on disk. |
| `SendLogEnabled` | client | Also stream the BepInEx log: a bounded snapshot of what is already on disk at plugin start (the disk listener is flushed first), then live lines at Info and above from every log source. The relayed copy is a superset of `LogOutput.log`: it includes the game's own Unity log lines (`ZLog`, errors and warnings included), which BepInEx omits from the disk log by default. The log names Steam IDs, characters and worlds; enable it only towards a server you trust. |
| `AcceptEnabled` | server | Accept relayed files into `captures/remote`. |
| `MaxDirectoryMiB` | server | Total allowance under `captures/remote` (default 1024). A stream that would exceed it is closed with a trailer. |
| `PurgeOldestAtStart` | server | At startup, delete the oldest relayed files until `captures/remote` holds at most three quarters of `MaxDirectoryMiB` (default on). |
| `MaxBytesPerSecond` | client | Cap on relayed bytes per second (default 65,536), on top of the idle-socket rule below. |

## Why a trickle and not "on exit" or "on save"

`ZNet.StopAll` sends the disconnect and disposes the socket in the same frame, so there is no
moment at exit to send anything. A save is the wrong moment too: a 30-minute client capture is
33-42 MB raw, 7-9 MB compressed, which would occupy a 1 MB/s LAN link for 7-9 s exactly when
the game already hitches. Streaming costs about 16 KB/s compressed (one 75 KB record per second
at the measured 0.21 Deflate ratio), and the exit case comes free: everything up to the last
interval is already on the server, which closes the file with a trailer naming the reason. What
stays local-only is the capture's final export (`capture_end` and `writer_end`), because the
plugin stops the capture after the game has already disposed the socket (Steam closes it
without linger); the relayed copy ends with `relay_end` = `peer_disconnected` instead.

## Protocol

The server offers first. On each new connection a server with `AcceptEnabled` registers
`BP_RelayChunk` on the peer's `ZRpc` and sends `BP_RelayOffer` (int version, currently 1). A
client with either send key registers `BP_RelayOffer` on its server peer and only starts sending
once a compatible offer arrives, so a vanilla or older server receives one RPC it drops silently
(`ZRpc.HandlePackage` ignores an unregistered name) and nothing else.

A message is one JSONL record (or one batch of log lines), framed with `CompressionFrame` and
split into chunks of at most 8 KB. Each chunk is one `ZPackage`: file name, kind, sequence,
`Final` (last piece of the message), `End` (the file is complete), payload. Sequences are per
stream and start at 0; the receiver refuses a stream that does not, and a reconnect restarts
every stream under a `-r<n>` suffix rather than resuming one. Such a file begins mid-capture,
without a start record, so `summarize_capture.py` refuses it: the client's local file is the
complete one, the relayed copies are for the sessions nobody copies files after.

The client sends at most one chunk per frame, only when `ISocket.GetSendQueueSize()` is 0 for
the server link, and never above `MaxBytesPerSecond`. Game traffic is therefore never queued
behind a relay chunk, and the adaptive window in `NetworkFlow` sees at most one 8 KB chunk.

The receiver (`RelaySink`, one per peer) validates the file name against a fixed shape
(`<UTC>-<role>-<32 hex>[-r<n>].jsonl|log`, nothing else ever becomes a path), reassembles the
message, decodes the frame, and appends it to the file, one record per line. A gap, a corrupt
frame, an oversize message (4 MB), an oversize stream (256 MB), a ninth concurrent stream, a
spent quota or a write failure closes the stream with a trailer naming the reason and refuses
that name for the rest of the connection. A peer that disconnects gets `peer_disconnected`. A
capture's trailer is a `relay_end` record with the capture id; `summarize_capture.py` reports it.

## Cost

Client: one Deflate at Fastest per record on the capture writer thread (already below normal
priority), a bounded 16 MB outbox, and one small RPC per frame at most. Server: one inflate per
message and one file append. No CPU figure is claimed; `relay_*` gauges hold the counts.

## Telemetry

| Gauge | What it decides |
| --- | --- |
| `relay_send_connected` | 1 while a server has offered. 0 with the key on means the server does not accept, or runs an older plugin. |
| `relay_send_messages`, `relay_send_message_bytes`, `relay_send_chunks`, `relay_send_bytes` | Records queued and chunks sent this interval, raw versus wire. Chunks stuck at 0 with messages rising means the socket is never idle or the rate cap is too low. |
| `relay_send_pending_bytes`, `relay_send_throttled`, `relay_send_dropped_messages`, `relay_send_dropped_bytes` | Backlog, frames skipped by the byte cap, and messages dropped whole when the 16 MB outbox is full. Steady drops mean the link cannot carry the capture and the server file has holes. |
| `relay_send_log_snapshot_bytes`, `relay_send_log_lines_dropped` | Size of the log snapshot taken at start, and live lines lost to the 256 KB line buffer. |
| `relay_sink_peers`, `relay_sink_offers_sent`, `relay_sink_streams_opened/closed/open` | Peers with a sink, offers made, and stream lifecycle on the server. |
| `relay_sink_chunks`, `relay_sink_bytes_received`, `relay_sink_messages`, `relay_sink_bytes_written`, `relay_sink_quota_used_bytes` | What arrived and what landed on disk; written over received is the compression ratio seen by the server. |
| `relay_sink_rejected`, `relay_failures` | Chunks refused (name, sequence, size, quota, corrupt frame) and hook exceptions; after 16 exceptions the module disables itself and the status reads `failed`. |

Labels: `relay_status` (`disabled`, `send`, `accept`, `send+accept`, `failed`,
`unavailable_unexpected_shape`) and `relay_scope`.

## Contract check and privacy

`Install` refuses to patch unless `ZNet.OnNewConnection(ZNetPeer)` and `ZNet.Disconnect(ZNetPeer)`
keep their signatures, `ZNetPeer` exposes `m_rpc`, `m_socket` and `m_server`,
`ISocket.GetSendQueueSize()` returns int, and `ZRpc.HandlePackage` still drops an unknown name;
otherwise the status is `unavailable_unexpected_shape`. `CaptureRelayGameTests` asserts the same
from Mono.Cecil metadata, plus that `ZRpc.Serialize` has no `byte[]` case (the reason a chunk
is one `ZPackage`). `CaptureRelayTests` proves offline that a writer's output tees through the
outbox into a sink byte for byte, footer included.

Captures carry no player names, IDs, addresses or save contents (see the capture guide); the
relay adds nothing to them. The log does carry identities, which is why `SendLogEnabled` is a
separate key, off by default, and documented as a private-server option.
