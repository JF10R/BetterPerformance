using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Framed Deflate on Steam sockets, negotiated per connection. A frame is self-identifying
    // (magic, declared length, and a Deflate stream that must inflate to exactly that length),
    // so the decoder is always armed and depends on nothing the peer sent before: there is no
    // ordering rule to get wrong. Sending turns on for one direction the moment that side
    // receives the peer's offer on a compatible version, so a peer without this plugin never
    // sends an offer and is never sent a frame. An unframed packet stays valid either way, so
    // a mismatch degrades to uncompressed traffic, never to garbage. Once installed, decoding
    // never stops: a failure or the option turned off only stops offering and encoding.
    // Cost: Deflate at Fastest on the ~1-10 KB packets Valheim sends about 20 times a second
    // per peer. A received header is read in place; only a frame is inflated and rebuilt.
    // Measured ratio and CPU belong in the session capture, not here.
    internal static class NetworkCompression
    {
        private const int FailureLimit = 8;
        internal const int ProtocolVersion = 1;
        private const string OfferRpc = "BP_NetCompressOffer";
        private const string BetterNetworkingId = "CW_Jesse.BetterNetworking";

        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".NetworkCompression");
        private static readonly Dictionary<ZSteamSocket, State> States = new Dictionary<ZSteamSocket, State>();
        private static ConfigEntry<bool>? option;
        private static AccessTools.FieldRef<ZSteamSocket, Queue<byte[]>>? queueRef;
        // Null when ZPackage no longer has a MemoryStream m_stream: the decoder then copies
        // each packet with GetArray, as before, instead of refusing to install.
        private static AccessTools.FieldRef<ZPackage, MemoryStream>? streamRef;
        private static bool failed;
        private static long compressPackets, compressRawBytes, compressWireBytes, compressKeptRaw;
        private static long decodePackets, decodeRawBytes, decodeWireBytes, decodeFailures, unframedReceived;
        private static long peersOffered, peersIncompatible, failures, failureTotal;
        // Stopwatch ticks: one pair per encoding flush and per framed packet received.
        private static long compressTicks, compressMaxTicks, decodeTicks, decodeMaxTicks;

        internal static bool Installed { get; private set; }
        // Gates offering and encoding only. Decoding is gated on Installed alone: the peer
        // keeps sending frames after we stop, and those must never reach the game raw.
        internal static bool Enabled => Installed && !failed && option != null && option.Value;
        internal static string Status { get; private set; } = "disabled";

        private sealed class State
        {
            internal bool SendStarted;
            // Arrays already handed to Encode this flush. Byte arrays hash and compare by
            // reference, so a partially flushed queue is never encoded twice.
            internal readonly HashSet<byte[]> Encoded = new HashSet<byte[]>();
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            try
            {
                option = config.Bind("Network", "CompressionEnabled", false,
                    "Compresses Steam packets to peers that run the same version of this plugin, negotiated per connection. A peer without the plugin keeps receiving uncompressed traffic. Replaces BetterNetworking's compression and yields to it when that mod is present. Requires a restart.");
                // Off means no patch at all: the send hook sits on every queued packet.
                if (!option.Value) { Installed = false; Status = "disabled"; return; }
                if (BetterNetworkingLoaded())
                {
                    Installed = false;
                    Status = "yield_to_betternetworking";
                    logger.LogInfo("Network compression not installed: BetterNetworking already compresses this connection.");
                    return;
                }
                Verify();
                string? foreign = ForeignOwner(Contract.SendQueued!) ?? ForeignOwner(Contract.Recv!);
                if (foreign != null)
                {
                    Installed = false;
                    Status = "foreign_patch:" + foreign;
                    logger.LogInfo("Network compression not installed: another mod already patches the Steam socket (" + foreign + ").");
                    return;
                }
                Patches.Patch(Contract.SendQueued!,
                    prefix: new HarmonyMethod(typeof(NetworkCompression), nameof(BeforeSend)),
                    postfix: new HarmonyMethod(typeof(NetworkCompression), nameof(AfterSend)));
                Patches.Patch(Contract.Recv!, postfix: new HarmonyMethod(typeof(NetworkCompression), nameof(AfterRecv)));
                Patches.Patch(Contract.NewConnection!, postfix: new HarmonyMethod(typeof(NetworkCompression), nameof(AfterNewConnection)));
                Patches.Patch(Contract.Disconnect!, postfix: new HarmonyMethod(typeof(NetworkCompression), nameof(AfterDisconnect)));
                queueRef = AccessTools.FieldRefAccess<ZSteamSocket, Queue<byte[]>>("m_sendQueue");
                // Optional: a miss here costs one copy per packet, never the install.
                try
                {
                    var stream = AccessTools.DeclaredField(typeof(ZPackage), "m_stream");
                    streamRef = stream != null && !stream.IsStatic && stream.FieldType == typeof(MemoryStream)
                        ? AccessTools.FieldRefAccess<ZPackage, MemoryStream>(stream) : null;
                }
                catch { streamRef = null; }
                Installed = true;
                failed = false;
                Status = "installed";
                logger.LogInfo("Network compression installed; each connection negotiates it and stays uncompressed when the peer does not answer.");
            }
            catch (Exception exception)
            {
                Installed = false;
                Status = "unavailable_unexpected_shape";
                // A failed rollback must not escape: Installed=false already makes every hook
                // a no-op, and an escaping exception would abort plugin start-up.
                try { PatchRemoval.UnpatchOwned(Patches); } catch { Interlocked.Increment(ref failures); }
                logger.LogWarning("Network compression unavailable; traffic stays uncompressed: " + exception.Message);
            }
        }

        // Chainloader's static initializer needs the BepInEx runtime; outside the game (the
        // contract harness) it throws, which must read as "not loaded", not as a broken install.
        private static bool BetterNetworkingLoaded()
        {
            try { return Chainloader.PluginInfos.ContainsKey("CW_Jesse.BetterNetworking"); }
            catch { return false; }
        }

        private static class Contract
        {
            internal static MethodInfo? SendQueued, Recv, Send, NewConnection, Disconnect;
            internal static FieldInfo? SendQueue;
        }

        // Shape checks only. Every member the hooks touch must exist with the exact expected
        // signature; anything else throws so Install falls back to uncompressed traffic.
        internal static void Verify()
        {
            Contract.SendQueued = AccessTools.DeclaredMethod(typeof(ZSteamSocket), "SendQueuedPackages", Type.EmptyTypes);
            Contract.Recv = AccessTools.DeclaredMethod(typeof(ZSteamSocket), "Recv", Type.EmptyTypes);
            Contract.Send = AccessTools.DeclaredMethod(typeof(ZSteamSocket), "Send", new[] { typeof(ZPackage) });
            Contract.SendQueue = AccessTools.DeclaredField(typeof(ZSteamSocket), "m_sendQueue");
            Contract.NewConnection = AccessTools.DeclaredMethod(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) });
            Contract.Disconnect = AccessTools.DeclaredMethod(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
            if (Contract.SendQueued == null || Contract.SendQueued.IsStatic || Contract.SendQueued.IsPublic || Contract.SendQueued.ReturnType != typeof(void))
                throw new InvalidOperationException("ZSteamSocket.SendQueuedPackages() is not a private instance void method.");
            if (Contract.Recv == null || Contract.Recv.IsStatic || !Contract.Recv.IsPublic || Contract.Recv.ReturnType != typeof(ZPackage))
                throw new InvalidOperationException("ZSteamSocket.Recv() no longer returns a ZPackage.");
            if (Contract.Send == null || Contract.Send.IsStatic || !Contract.Send.IsPublic)
                throw new InvalidOperationException("ZSteamSocket.Send(ZPackage) is not a public instance method.");
            if (Contract.SendQueue == null || Contract.SendQueue.IsStatic || Contract.SendQueue.FieldType != typeof(Queue<byte[]>))
                throw new InvalidOperationException("ZSteamSocket.m_sendQueue is not a Queue<byte[]>.");
            if (Contract.NewConnection == null || Contract.NewConnection.IsStatic || Contract.NewConnection.IsPublic || Contract.NewConnection.ReturnType != typeof(void))
                throw new InvalidOperationException("ZNet.OnNewConnection(ZNetPeer) is not a private instance void method.");
            if (Contract.Disconnect == null || Contract.Disconnect.IsStatic || !Contract.Disconnect.IsPublic || Contract.Disconnect.ReturnType != typeof(void))
                throw new InvalidOperationException("ZNet.Disconnect(ZNetPeer) is not a public instance void method.");
            // The whole design rests on one Steam message per queue entry and one ZPackage per
            // received message: batching or splitting elsewhere would break the framing.
            RequireSteamTransport(PatchProcessor.GetOriginalInstructions(Contract.SendQueued), PatchProcessor.GetOriginalInstructions(Contract.Recv));
        }

        internal static void RequireSteamTransport(List<CodeInstruction> send, List<CodeInstruction> receive)
        {
            if (!send.Exists(instruction => instruction.operand is MethodInfo method && method.Name == "SendMessageToConnection"))
                throw new InvalidOperationException("ZSteamSocket.SendQueuedPackages no longer sends each queued array as one Steam message.");
            if (!receive.Exists(instruction => instruction.operand is MethodInfo method && method.Name == "ReceiveMessagesOnConnection"))
                throw new InvalidOperationException("ZSteamSocket.Recv no longer builds one package per received Steam message.");
        }

        private static string? ForeignOwner(MethodInfo method)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return null;
            foreach (string owner in info.Owners)
                if (owner != Patches.Id) return owner.Length <= 64 ? owner : owner.Substring(0, 64);
            return null;
        }

        private static State? Lookup(ZSteamSocket socket)
        {
            lock (States) return States.TryGetValue(socket, out var state) ? state : null;
        }

        // Both roles run OnNewConnection, so both offer and each direction turns on by itself
        // once the other side's offer arrives. The offer costs one tiny RPC; a peer that does
        // not know the name drops it silently (ZRpc.HandlePackage ignores an unregistered hash).
        private static void AfterNewConnection(ZNetPeer peer)
        {
            if (!Enabled || peer == null || peer.m_rpc == null) return;
            try
            {
                peer.m_rpc.Register<int>(OfferRpc, (_, version) => OnOffer(peer, version));
                peer.m_rpc.Invoke(OfferRpc, ProtocolVersion);
            }
            catch { Fail(); }
        }

        // The peer offered, so the peer decodes: compression on this direction can start with
        // this message and needs no acknowledgement and no ordering against the send queue.
        private static void OnOffer(ZNetPeer peer, int version)
        {
            if (!Enabled || peer == null) return;
            try
            {
                if (version != ProtocolVersion) { Interlocked.Increment(ref peersIncompatible); return; }
                var socket = SteamTelemetry.SteamSocket(peer.m_socket);
                if (socket == null) return;
                lock (States) States[socket] = new State { SendStarted = true };
                Interlocked.Increment(ref peersOffered);
            }
            catch { Fail(); }
        }

        private static void AfterDisconnect(ZNetPeer peer)
        {
            if (!Installed || peer == null) return;
            try
            {
                var socket = SteamTelemetry.SteamSocket(peer.m_socket);
                if (socket == null) return;
                lock (States) States.Remove(socket);
            }
            catch { Fail(); }
        }

        private static void BeforeSend(ZSteamSocket __instance)
        {
            if (!Enabled || __instance == null || queueRef == null) return;
            try
            {
                var state = Lookup(__instance);
                if (state == null || !state.SendStarted) return;
                var queue = queueRef(__instance);
                if (queue == null || queue.Count == 0) return;
                long started = Stopwatch.GetTimestamp();
                // Rotate the queue once so order is preserved and each array is encoded once.
                for (int i = 0, count = queue.Count; i < count; i++)
                    queue.Enqueue(EncodeOnce(state, queue.Dequeue()));
                AddElapsed(ref compressTicks, ref compressMaxTicks, started);
            }
            catch { Fail(); }
        }

        private static void AddElapsed(ref long sum, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref sum, elapsed);
            long seen;
            while (elapsed > (seen = Interlocked.Read(ref max)) && Interlocked.CompareExchange(ref max, elapsed, seen) != seen) { }
        }

        private static double Milliseconds(ref long ticks) => Interlocked.Exchange(ref ticks, 0) * 1000.0 / Stopwatch.Frequency;

        private static byte[] EncodeOnce(State state, byte[] item)
        {
            if (state.Encoded.Contains(item)) return item;
            byte[] encoded = CompressionFrame.Encode(item);
            state.Encoded.Add(encoded);
            if (ReferenceEquals(encoded, item)) { Interlocked.Increment(ref compressKeptRaw); return item; }
            Interlocked.Increment(ref compressPackets);
            Interlocked.Add(ref compressRawBytes, item.Length);
            Interlocked.Add(ref compressWireBytes, encoded.Length);
            return encoded;
        }

        private static void AfterSend(ZSteamSocket __instance)
        {
            if (!Enabled || __instance == null || queueRef == null) return;
            try
            {
                var state = Lookup(__instance);
                if (state == null) return;
                var queue = queueRef(__instance);
                // Nothing is pending, so no array in flight can be encoded a second time.
                if (queue != null && queue.Count == 0) state.Encoded.Clear();
            }
            catch { Fail(); }
        }

        // Gated on Installed, never on Enabled: after a failure or with the option turned off
        // the peer still sends frames, and only this hook stands between them and the game.
        private static void AfterRecv(ZSteamSocket __instance, ref ZPackage __result)
        {
            if (__result == null || !Installed || __instance == null) return;
            try
            {
                // Recv builds the package with new ZPackage(byte[]), an expandable stream whose
                // buffer is exposable: read the header in place. Anything else costs one copy.
                byte[] data;
                int offset, count;
                var access = streamRef;
                MemoryStream? stream = access == null ? null : access(__result);
                if (stream != null && stream.GetType() == typeof(MemoryStream) && stream.TryGetBuffer(out var segment) && segment.Array != null)
                { data = segment.Array; offset = segment.Offset; count = segment.Count; }
                else
                {
                    data = __result.GetArray();
                    if (data == null) return;
                    offset = 0; count = data.Length;
                }
                // The frame identifies itself, so the decoder needs no per-socket flag. The
                // raw counter stays keyed on the state, where it still means something: raw
                // packets from a peer that does run this plugin.
                if (!CompressionFrame.IsFramed(data, offset, count))
                {
                    if (Lookup(__instance) != null) Interlocked.Increment(ref unframedReceived);
                    return;
                }
                long started = Stopwatch.GetTimestamp();
                // A framed header with an unusable body is left alone: the game rejects the
                // resulting RPC, which is strictly better than throwing out of the transport.
                bool decoded = CompressionFrame.TryDecode(data, offset, count, out byte[] raw);
                if (decoded) __result = new ZPackage(raw);
                AddElapsed(ref decodeTicks, ref decodeMaxTicks, started);
                if (!decoded) { Interlocked.Increment(ref decodeFailures); return; }
                Interlocked.Increment(ref decodePackets);
                Interlocked.Add(ref decodeRawBytes, raw.Length);
                Interlocked.Add(ref decodeWireBytes, count);
            }
            catch { Fail(); }
        }

        // Stops offering and encoding only (Enabled reads the flag); AfterRecv never does, so a
        // failure anywhere, the decoder included, cannot leave a peer's frames undecoded.
        private static void Fail()
        {
            Interlocked.Increment(ref failures);
            if (Interlocked.Increment(ref failureTotal) < FailureLimit) return;
            // Do not unpatch from inside a patch: the flag alone stops the send side.
            failed = true;
            Status = "failed";
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref compressPackets, 0);
            Interlocked.Exchange(ref compressRawBytes, 0);
            Interlocked.Exchange(ref compressWireBytes, 0);
            Interlocked.Exchange(ref compressKeptRaw, 0);
            Interlocked.Exchange(ref decodePackets, 0);
            Interlocked.Exchange(ref decodeRawBytes, 0);
            Interlocked.Exchange(ref decodeWireBytes, 0);
            Interlocked.Exchange(ref decodeFailures, 0);
            Interlocked.Exchange(ref unframedReceived, 0);
            Interlocked.Exchange(ref peersOffered, 0);
            Interlocked.Exchange(ref peersIncompatible, 0);
            Interlocked.Exchange(ref failures, 0);
            Interlocked.Exchange(ref compressTicks, 0);
            Interlocked.Exchange(ref compressMaxTicks, 0);
            Interlocked.Exchange(ref decodeTicks, 0);
            Interlocked.Exchange(ref decodeMaxTicks, 0);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            int active = 0;
            lock (States)
            {
                // A disconnect we never saw would leak one entry per socket; sweep them here.
                List<ZSteamSocket>? stale = null;
                foreach (var pair in States)
                {
                    bool connected;
                    try { connected = pair.Key.IsConnected(); } catch { connected = false; }
                    if (!connected) (stale ?? (stale = new List<ZSteamSocket>())).Add(pair.Key);
                    else if (pair.Value.SendStarted) active++;
                }
                if (stale != null) foreach (var socket in stale) States.Remove(socket);
            }
            gauges.Add(new NumberValue("net_compress_compress_packets", Interlocked.Exchange(ref compressPackets, 0), "packets"));
            gauges.Add(new NumberValue("net_compress_compress_raw_bytes", Interlocked.Exchange(ref compressRawBytes, 0), "bytes"));
            gauges.Add(new NumberValue("net_compress_compress_wire_bytes", Interlocked.Exchange(ref compressWireBytes, 0), "bytes"));
            gauges.Add(new NumberValue("net_compress_compress_kept_raw", Interlocked.Exchange(ref compressKeptRaw, 0), "packets"));
            gauges.Add(new NumberValue("net_compress_decode_packets", Interlocked.Exchange(ref decodePackets, 0), "packets"));
            gauges.Add(new NumberValue("net_compress_decode_raw_bytes", Interlocked.Exchange(ref decodeRawBytes, 0), "bytes"));
            gauges.Add(new NumberValue("net_compress_decode_wire_bytes", Interlocked.Exchange(ref decodeWireBytes, 0), "bytes"));
            gauges.Add(new NumberValue("net_compress_decode_failures", Interlocked.Exchange(ref decodeFailures, 0), "packets"));
            gauges.Add(new NumberValue("net_compress_unframed_received", Interlocked.Exchange(ref unframedReceived, 0), "packets"));
            gauges.Add(new NumberValue("net_compress_peers_active", active, "peers"));
            gauges.Add(new NumberValue("net_compress_peers_offered", Interlocked.Exchange(ref peersOffered, 0), "peers"));
            gauges.Add(new NumberValue("net_compress_peers_incompatible", Interlocked.Exchange(ref peersIncompatible, 0), "peers"));
            gauges.Add(new NumberValue("net_compress_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            // Elapsed time inside the hooks (sum and worst per export): encoding flushes, and framed packets
            // inflated and rebuilt. The in-place header test on raw packets is not timed.
            gauges.Add(new NumberValue("net_compress_compress_ms", Milliseconds(ref compressTicks), "ms"));
            gauges.Add(new NumberValue("net_compress_compress_max_ms", Milliseconds(ref compressMaxTicks), "ms"));
            gauges.Add(new NumberValue("net_compress_decode_ms", Milliseconds(ref decodeTicks), "ms"));
            gauges.Add(new NumberValue("net_compress_decode_max_ms", Milliseconds(ref decodeMaxTicks), "ms"));
            labels.Add(new TextValue("net_compress_status", !Installed ? Status : failed ? "failed" : Enabled ? "enabled" : "installed_disabled"));
            labels.Add(new TextValue("net_compress_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("net_compress_version", ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            labels.Add(new TextValue("net_compress_scope", "steamworks_only; framed_deflate; peers_without_this_plugin_stay_raw"));
        }

        internal static void Uninstall()
        {
            Reset();
            try { PatchRemoval.UnpatchOwned(Patches); } catch { }
            lock (States) States.Clear();
            Installed = false;
            failed = false;
            Status = "disabled";
            option = null;
            queueRef = null;
            streamRef = null;
            Interlocked.Exchange(ref failureTotal, 0);
        }
    }
}
