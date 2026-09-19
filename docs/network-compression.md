# Network compression

## What it replaces

BetterNetworking compresses every Steam packet with zstd and falls back on a heuristic when a
message does not look compressed. This module does the same job with framed Deflate and a
negotiated handshake instead of a guess. It yields outright when BetterNetworking is loaded:
nothing is patched and the status reads `yield_to_betternetworking`. Steamworks sockets only,
a PlayFab peer is never compressed. Config key `Network.CompressionEnabled`, off by default,
needs a restart.

## Protocol

A frame is `'B','P','Z','1'`, the int32 little-endian raw length, then Deflate at Fastest.
`CompressionFrame.Encode` returns the frame only when it is strictly smaller than the input,
otherwise the input itself, so framing can never add bytes. A frame identifies itself: the
magic, the declared length, and a Deflate stream that must inflate to exactly that length.

Decoding is therefore unconditional and stateless. The `Recv` postfix tries `TryDecode` on any
framed payload, on every Steam socket, whatever the peer sent before: nothing to arm, and no
ordering rule to get wrong.

Sending turns on per direction, on the offer alone. On each new connection both sides register
`BP_NetCompressOffer` (int version) on the peer's `ZRpc` and send an offer carrying version 1.
Receiving a compatible offer means the peer decodes, so this side starts compressing towards it
immediately, with no acknowledgement. The two directions turn on independently. A peer without
the plugin never registers the name and never sends an offer, so `ZRpc.HandlePackage` drops
ours silently and that connection stays raw in both directions; a peer on another protocol
version is counted incompatible and also stays raw. An unframed packet is always valid, so any
mismatch degrades to vanilla traffic rather than to garbage.

## Cost

Deflate at Fastest over the roughly 1-10 KB packets Valheim sends about 20 times a second per
peer, plus one array copy per decoded message. The send prefix rotates the queue once and
encodes each array exactly once, tracked by reference in a per-socket set cleared when the
queue empties. No CPU or ratio figure is claimed here; the capture holds it.

## Telemetry

| Gauge | What it decides |
| --- | --- |
| `net_compress_peers_active` | Sockets compressing outbound. Zero with peers connected means no offer arrived and the module is inert. |
| `net_compress_peers_offered`, `net_compress_peers_incompatible` | Offers accepted, one per direction per connection, and offers refused for a version other than 1, which means two plugin versions share the session. |
| `net_compress_compress_packets`, `net_compress_compress_raw_bytes`, `net_compress_compress_wire_bytes` | Packets that went out framed, and the ratio to judge the module on. Wire close to raw means Deflate is not paying for itself here. |
| `net_compress_compress_kept_raw` | Packets Deflate could not shrink, sent unchanged. A large share means dense payloads. |
| `net_compress_decode_packets`, `net_compress_decode_raw_bytes`, `net_compress_decode_wire_bytes` | The inbound mirror of the three above, read the same way. |
| `net_compress_unframed_received` | Raw packets received from a peer that did offer. Normal for its incompressible packets; a steady high share says that peer compresses almost nothing. |
| `net_compress_decode_failures`, `net_compress_failures` | Framed headers that would not decode, which is a bug or a raw payload starting with the magic, and hook exceptions. The undecodable packet passes through untouched; after 8 exceptions the module disables itself and the status reads `failed`. |

Labels: `net_compress_status`, `net_compress_enabled`, `net_compress_version`, `net_compress_scope`.

## Contract check and coexistence

`Install` refuses to patch unless `ZSteamSocket.SendQueuedPackages` is the private no-argument
method handing each `m_sendQueue` entry to `SendMessageToConnection`, `Recv` builds one
`ZPackage` per `ReceiveMessagesOnConnection` message, and `ZNet.OnNewConnection` and
`ZNet.Disconnect` keep their signatures; otherwise the status is
`unavailable_unexpected_shape` and traffic stays uncompressed. It also stands down with
`foreign_patch:<owner>` when another Harmony owner already sits on `SendQueuedPackages` or
`Recv`. Per-socket state is dropped on `ZNet.Disconnect` and swept on every export.
`NetworkCompressionGameTests` asserts all of it from Mono.Cecil metadata, because
`ZSteamSocket` cannot be type-loaded by a standalone CLR, and asserts that the receive hook
reaches no per-socket state before the framing test.

## What not to do

Do not detect compressed input heuristically, and do not put the decoder behind a negotiation
flag: decode on frame identity, so no ordering between offer and first frame can matter. Do not
send a frame to a peer that has not offered, or extend this to PlayFab without a contract test.
