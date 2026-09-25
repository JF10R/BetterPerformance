using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Mirrors a client's capture records, and optionally its BepInEx log, to the server it is
    // connected to, as a trickle in the background: one chunk of at most 8 KB per frame, only
    // when the peer's send queue is empty, under a byte-per-second cap. The exit case costs
    // nothing extra: every record is already on the server by the time the client quits, at
    // most the last second is missing, and the server closes the file with a trailer naming
    // why. The server offers first (BP_RelayOffer); a client never sends to a server that did
    // not, so a vanilla or older server sees one RPC it drops silently. Files land in
    // captures/remote under the client's own file name, which is validated to a fixed shape
    // before it becomes a path. Config section [Relay]; every key off by default.
    internal static class CaptureRelay
    {
        internal const int ProtocolVersion = 1;
        private const string OfferRpc = "BP_RelayOffer";
        private const string ChunkRpc = "BP_RelayChunk";
        private const int FailureLimit = 16;
        private const long MaxPendingBytes = 16L * 1024 * 1024;
        private const long MaxLogSnapshotBytes = 4L * 1024 * 1024;

        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".CaptureRelay");
        private static readonly RelayOutbox Outbox = new RelayOutbox(MaxPendingBytes);
        private static readonly RelayLineBatcher LogLines = new RelayLineBatcher();
        private static readonly Dictionary<ZRpc, RelaySink> Sinks = new Dictionary<ZRpc, RelaySink>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static ConfigEntry<bool>? sendCaptures, sendLog, accept, purgeAtStart;
        private static ConfigEntry<int>? directoryLimit, rateLimit;
        private static ManualLogSource? log;
        private static LogMirror? mirror;
        private static RelayQuota? quota;
        private static string remoteDirectory = "";
        private static string logStreamBase = "";
        private static int generation;
        private static Target? target;
        private static bool failed;
        private static long failures, failureTotal, throttled, offersReceived, offersSent, chunksReceived, logSnapshotBytes;
        private static double windowStart;
        private static long windowBytes;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal static bool Sending => Installed && !failed && sendCaptures != null && sendCaptures.Value;
        internal static bool SendingLog => Installed && !failed && sendLog != null && sendLog.Value;
        internal static bool Accepting => Installed && !failed && accept != null && accept.Value;

        private sealed class Target
        {
            internal ZNetPeer Peer = null!;
            internal ZRpc Rpc = null!;
            internal ISocket Socket = null!;
        }

        internal static void Install(ConfigFile config, ManualLogSource logger, string captureDirectory)
        {
            log = logger;
            try
            {
                sendCaptures = config.Bind("Relay", "SendCapturesEnabled", false,
                    "Client: stream this process's capture records to the server as they are written, when that server accepts them (its Relay.AcceptEnabled). Captures carry no names, IDs or addresses. About 16 KB/s compressed, sent only while the connection is otherwise idle. Requires a restart.");
                sendLog = config.Bind("Relay", "SendLogEnabled", false,
                    "Client: also stream BepInEx/LogOutput.log (a snapshot at start, then live lines). The log contains Steam IDs, character and world names: enable only towards a server you trust. Requires a restart.");
                accept = config.Bind("Relay", "AcceptEnabled", false,
                    "Server: accept relayed captures and logs from clients into BepInEx/BetterPerformance/captures/remote. Requires a restart.");
                directoryLimit = config.Bind("Relay", "MaxDirectoryMiB", 1024, new ConfigDescription(
                    "Server: total size allowed under captures/remote. A stream that would exceed it is closed with a trailer. See PurgeOldestAtStart.", new AcceptableValueRange<int>(64, 16384)));
                purgeAtStart = config.Bind("Relay", "PurgeOldestAtStart", true,
                    "Server: at startup, delete the oldest relayed files until captures/remote holds at most three quarters of MaxDirectoryMiB. Off: never delete; relaying stops once the directory is full.");
                rateLimit = config.Bind("Relay", "MaxBytesPerSecond", 65536, new ConfigDescription(
                    "Client: cap on relayed bytes per second, on top of the idle-socket rule.", new AcceptableValueRange<int>(8192, 1048576)));
                if (!sendCaptures.Value && !sendLog.Value && !accept.Value) { Installed = false; Status = "disabled"; return; }
                Verify();
                remoteDirectory = Path.Combine(captureDirectory, "remote");
                Patches.Patch(Contract.NewConnection!, postfix: new HarmonyMethod(typeof(CaptureRelay), nameof(AfterNewConnection)));
                Patches.Patch(Contract.Disconnect!, postfix: new HarmonyMethod(typeof(CaptureRelay), nameof(AfterDisconnect)));
                if (accept.Value)
                {
                    long limit = directoryLimit.Value * 1024L * 1024L;
                    if (purgeAtStart.Value)
                    {
                        CaptureStorage.TrimTo(remoteDirectory, limit / 4 * 3, out int deleted, out long freed);
                        if (deleted > 0) logger.LogInfo("Capture relay: deleted the " + deleted + " oldest relayed files (" + (freed / (1024 * 1024)) + " MiB).");
                    }
                    quota = new RelayQuota(limit, DirectorySize(remoteDirectory));
                }
                if (sendLog.Value) StartLogMirror();
                Installed = true;
                failed = false;
                Status = "installed";
                logger.LogInfo("Capture relay installed: send captures=" + sendCaptures.Value + ", send log=" + sendLog.Value + ", accept=" + accept.Value + ".");
            }
            catch (Exception exception)
            {
                Installed = false;
                Status = "unavailable_unexpected_shape";
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                logger.LogWarning("Capture relay unavailable: " + exception.Message);
            }
        }

        private static class Contract
        {
            internal static MethodInfo? NewConnection, Disconnect;
        }

        // Shape checks only: the two hooks, the peer fields the hooks read, and the socket
        // predicate the pump gates on. The RPC and package calls are typed at compile time.
        internal static void Verify()
        {
            Contract.NewConnection = AccessTools.DeclaredMethod(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) });
            Contract.Disconnect = AccessTools.DeclaredMethod(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
            if (Contract.NewConnection == null || Contract.NewConnection.IsStatic || Contract.NewConnection.IsPublic || Contract.NewConnection.ReturnType != typeof(void))
                throw new InvalidOperationException("ZNet.OnNewConnection(ZNetPeer) is not a private instance void method.");
            if (Contract.Disconnect == null || Contract.Disconnect.IsStatic || !Contract.Disconnect.IsPublic || Contract.Disconnect.ReturnType != typeof(void))
                throw new InvalidOperationException("ZNet.Disconnect(ZNetPeer) is not a public instance void method.");
            var isServer = AccessTools.DeclaredMethod(typeof(ZNet), "IsServer", Type.EmptyTypes);
            if (isServer == null || isServer.IsStatic || isServer.ReturnType != typeof(bool))
                throw new InvalidOperationException("ZNet.IsServer() is not an instance bool method.");
            foreach (string name in new[] { "m_rpc", "m_socket", "m_server" })
            {
                var field = AccessTools.DeclaredField(typeof(ZNetPeer), name);
                if (field == null || field.IsStatic) throw new InvalidOperationException("ZNetPeer." + name + " is not an instance field.");
            }
            var queueSize = typeof(ISocket).GetMethod("GetSendQueueSize", Type.EmptyTypes);
            if (queueSize == null || queueSize.ReturnType != typeof(int))
                throw new InvalidOperationException("ISocket.GetSendQueueSize() does not return int.");
            var handle = AccessTools.DeclaredMethod(typeof(ZRpc), "HandlePackage", new[] { typeof(ZPackage) });
            if (handle == null || !PatchProcessor.GetOriginalInstructions(handle).Exists(i => i.operand is MethodInfo m && m.Name == "TryGetValue"))
                throw new InvalidOperationException("ZRpc.HandlePackage no longer ignores an unregistered RPC name, so the offer would break a vanilla peer.");
        }

        private static long DirectorySize(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return 0;
                long total = 0;
                foreach (string file in Directory.GetFiles(directory)) total += new FileInfo(file).Length;
                return total;
            }
            catch { return 0; }
        }

        // ---- sender ----

        // The tee the capture session installs on its writer: every encoded line, on the writer
        // thread, framed and queued under the capture's own file name.
        internal static Action<byte[]>? Tee(string fileName)
        {
            if (!Sending || string.IsNullOrEmpty(fileName)) return null;
            return line => Outbox.Enqueue(StreamName(fileName), RelayStreamKind.Capture, line);
        }

        // Called once the writer has finished (its writer_end record is already queued).
        internal static void CaptureFinished(string outputPath)
        {
            if (!Sending || string.IsNullOrEmpty(outputPath)) return;
            try { Outbox.EndStream(StreamName(Path.GetFileName(outputPath)), RelayStreamKind.Capture); }
            catch { Fail(); }
        }

        // A reconnect restarts every stream under a suffixed name, because the receiver refuses
        // a stream that does not start at sequence 0.
        private static string StreamName(string fileName)
        {
            int gen = Volatile.Read(ref generation);
            if (gen == 0) return fileName;
            int dot = fileName.LastIndexOf('.');
            return dot < 0 ? fileName : fileName.Substring(0, dot) + "-r" + gen.ToString(CultureInfo.InvariantCulture) + fileName.Substring(dot);
        }

        private static void StartLogMirror()
        {
            logStreamBase = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-client-" + Guid.NewGuid().ToString("N") + ".log";
            mirror = new LogMirror();
            Logger.Listeners.Add(mirror);
            // What BepInEx wrote before this plugin loaded (preloader, other plugins' start-up),
            // bounded; the live listener carries everything from here on. The disk listener
            // flushes on a timer, so flush it first or the last lines before us are in neither.
            try
            {
                foreach (var listener in Logger.Listeners)
                    if (listener is DiskLogListener disk) { try { disk.LogWriter?.Flush(); } catch { } }
                string path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                if (File.Exists(path))
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long length = Math.Min(stream.Length, MaxLogSnapshotBytes);
                        var bytes = new byte[length];
                        int read = 0;
                        while (read < length) { int step = stream.Read(bytes, read, (int)(length - read)); if (step <= 0) break; read += step; }
                        if (read > 0)
                        {
                            if (read < length) Array.Resize(ref bytes, read);
                            Outbox.Enqueue(StreamName(logStreamBase), RelayStreamKind.Log, bytes);
                            logSnapshotBytes = read;
                        }
                    }
                }
            }
            catch { Interlocked.Increment(ref failures); }
        }

        private sealed class LogMirror : ILogListener
        {
            private const LogLevel Kept = LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;
            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                try
                {
                    if (eventArgs == null || (eventArgs.Level & Kept) == 0) return;
                    LogLines.Append(eventArgs.ToStringLine(), Clock.Elapsed.TotalSeconds);
                }
                catch { Interlocked.Increment(ref failures); }
            }
            public void Dispose() { }
        }

        // Once per frame from the plugin's Update. One chunk, only when nothing else is queued
        // on the socket, under the byte-rate cap.
        internal static void Pump()
        {
            if (!Installed || failed) return;
            try
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (SendingLog)
                {
                    byte[]? batch = LogLines.TryTake(now);
                    if (batch != null) Outbox.Enqueue(StreamName(logStreamBase), RelayStreamKind.Log, batch);
                }
                var t = target;
                if (t == null) return;
                if (!t.Socket.IsConnected()) { target = null; return; }
                if (t.Socket.GetSendQueueSize() > 0) return;
                if (now - windowStart >= 1.0) { windowStart = now; windowBytes = 0; }
                if (rateLimit != null && windowBytes >= rateLimit.Value) { throttled++; return; }
                if (!Outbox.TryDequeue(out var chunk)) return;
                var pkg = new ZPackage();
                pkg.Write(chunk.Name);
                pkg.Write((byte)chunk.Kind);
                pkg.Write(chunk.Sequence);
                pkg.Write(chunk.Final);
                pkg.Write(chunk.End);
                pkg.Write(chunk.Payload);
                t.Rpc.Invoke(ChunkRpc, pkg);
                windowBytes += chunk.Payload.Length;
            }
            catch { Fail(); }
        }

        // ---- hooks ----

        private static void AfterNewConnection(ZNet __instance, ZNetPeer peer)
        {
            if (!Installed || failed || peer == null || peer.m_rpc == null || peer.m_socket == null) return;
            try
            {
                if (__instance.IsServer())
                {
                    if (!Accepting) return;
                    var rpc = peer.m_rpc;
                    var sink = new RelaySink(OpenRemoteFile, quota!);
                    lock (Sinks) Sinks[rpc] = sink;
                    rpc.Register<ZPackage>(ChunkRpc, OnChunk);
                    rpc.Invoke(OfferRpc, ProtocolVersion);
                    Interlocked.Increment(ref offersSent);
                }
                else if (Sending || SendingLog)
                {
                    if (!peer.m_server) return;
                    peer.m_rpc.Register<int>(OfferRpc, (_, version) => OnOffer(peer, version));
                }
            }
            catch { Fail(); }
        }

        private static void OnOffer(ZNetPeer peer, int version)
        {
            if (!Installed || failed || peer == null || version != ProtocolVersion) return;
            try
            {
                // A second offer in one process is a reconnect: the old server's streams are
                // over, the new one gets fresh names.
                if (target != null || offersReceived > 0) { Interlocked.Increment(ref generation); Outbox.Clear(); }
                target = new Target { Peer = peer, Rpc = peer.m_rpc, Socket = peer.m_socket };
                Interlocked.Increment(ref offersReceived);
                log?.LogInfo("Capture relay: the server accepts relayed files; streaming starts.");
            }
            catch { Fail(); }
        }

        private static void AfterDisconnect(ZNetPeer peer)
        {
            if (!Installed || peer == null) return;
            try
            {
                var t = target;
                if (t != null && ReferenceEquals(t.Peer, peer)) target = null;
                if (peer.m_rpc == null) return;
                RelaySink? sink;
                lock (Sinks) { if (Sinks.TryGetValue(peer.m_rpc, out sink)) Sinks.Remove(peer.m_rpc); }
                if (sink != null) { sink.CloseAll("peer_disconnected"); sink.Dispose(); }
            }
            catch { Fail(); }
        }

        // ---- receiver ----

        private static Stream OpenRemoteFile(string name, RelayStreamKind kind)
        {
            // The sink has validated the name against RelayNames; this is defence in depth.
            if (!RelayNames.IsValid(name, kind) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("invalid relay name");
            Directory.CreateDirectory(remoteDirectory);
            return new FileStream(Path.Combine(remoteDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }

        private static void OnChunk(ZRpc rpc, ZPackage pkg)
        {
            if (!Accepting || rpc == null || pkg == null) return;
            try
            {
                RelaySink? sink;
                lock (Sinks) Sinks.TryGetValue(rpc, out sink);
                if (sink == null) return;
                Interlocked.Increment(ref chunksReceived);
                var chunk = new RelayChunk { Name = pkg.ReadString(), Kind = (RelayStreamKind)pkg.ReadByte(), Sequence = pkg.ReadInt() };
                chunk.Final = pkg.ReadBool();
                chunk.End = pkg.ReadBool();
                chunk.Payload = pkg.ReadByteArray();
                sink.Accept(chunk);
            }
            catch { Fail(); }
        }

        private static void Fail()
        {
            Interlocked.Increment(ref failures);
            if (Interlocked.Increment(ref failureTotal) < FailureLimit) return;
            failed = true;
            Status = "failed";
        }

        internal static void Reset()
        {
            Outbox.Drain();
            Interlocked.Exchange(ref failures, 0);
            Interlocked.Exchange(ref throttled, 0);
            Interlocked.Exchange(ref chunksReceived, 0);
            Interlocked.Exchange(ref offersSent, 0);
            lock (Sinks) foreach (var sink in Sinks.Values) sink.Drain();
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            var send = Outbox.Drain();
            gauges.Add(new NumberValue("relay_send_messages", send.Messages, "messages"));
            gauges.Add(new NumberValue("relay_send_message_bytes", send.MessageBytes, "bytes"));
            gauges.Add(new NumberValue("relay_send_chunks", send.ChunksSent, "chunks"));
            gauges.Add(new NumberValue("relay_send_bytes", send.BytesSent, "bytes"));
            gauges.Add(new NumberValue("relay_send_dropped_messages", send.DroppedMessages, "messages"));
            gauges.Add(new NumberValue("relay_send_dropped_bytes", send.DroppedBytes, "bytes"));
            gauges.Add(new NumberValue("relay_send_pending_bytes", send.PendingBytes, "bytes"));
            gauges.Add(new NumberValue("relay_send_throttled", Interlocked.Exchange(ref throttled, 0), "frames"));
            gauges.Add(new NumberValue("relay_send_connected", target != null ? 1 : 0, "boolean"));
            gauges.Add(new NumberValue("relay_send_log_lines_dropped", LogLines.DroppedLines, "lines"));
            gauges.Add(new NumberValue("relay_send_log_snapshot_bytes", logSnapshotBytes, "bytes"));
            long chunks = 0, received = 0, written = 0, messages = 0, rejected = 0, sinkFailures = 0, opened = 0, closed = 0, open = 0;
            int peers;
            lock (Sinks)
            {
                peers = Sinks.Count;
                foreach (var sink in Sinks.Values)
                {
                    var s = sink.Drain();
                    chunks += s.Chunks; received += s.BytesReceived; written += s.BytesWritten; messages += s.Messages;
                    rejected += s.Rejected; sinkFailures += s.Failures; opened += s.StreamsOpened; closed += s.StreamsClosed; open += s.OpenStreams;
                }
            }
            gauges.Add(new NumberValue("relay_sink_peers", peers, "peers"));
            gauges.Add(new NumberValue("relay_sink_offers_sent", Interlocked.Exchange(ref offersSent, 0), "peers"));
            gauges.Add(new NumberValue("relay_sink_chunks", chunks, "chunks"));
            gauges.Add(new NumberValue("relay_sink_chunk_rpcs", Interlocked.Exchange(ref chunksReceived, 0), "calls"));
            gauges.Add(new NumberValue("relay_sink_bytes_received", received, "bytes"));
            gauges.Add(new NumberValue("relay_sink_bytes_written", written, "bytes"));
            gauges.Add(new NumberValue("relay_sink_messages", messages, "messages"));
            gauges.Add(new NumberValue("relay_sink_rejected", rejected, "chunks"));
            gauges.Add(new NumberValue("relay_sink_streams_opened", opened, "streams"));
            gauges.Add(new NumberValue("relay_sink_streams_closed", closed, "streams"));
            gauges.Add(new NumberValue("relay_sink_streams_open", open, "streams"));
            gauges.Add(new NumberValue("relay_sink_quota_used_bytes", quota?.Used ?? 0, "bytes"));
            gauges.Add(new NumberValue("relay_failures", Interlocked.Exchange(ref failures, 0) + sinkFailures, "calls"));
            labels.Add(new TextValue("relay_status", !Installed ? Status : failed ? "failed"
                : (Sending || SendingLog ? "send" : "") + (Accepting ? (Sending || SendingLog ? "+accept" : "accept") : "")));
            labels.Add(new TextValue("relay_scope", "client_to_server_only; captures_carry_no_identity; log_optional_and_identifying"));
        }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            if (mirror != null) { try { Logger.Listeners.Remove(mirror); } catch { } mirror = null; }
            lock (Sinks) { foreach (var sink in Sinks.Values) { sink.CloseAll("plugin_shutdown"); sink.Dispose(); } Sinks.Clear(); }
            Outbox.Clear();
            target = null;
            Installed = false;
            failed = false;
            Status = "disabled";
            sendCaptures = null; sendLog = null; accept = null; purgeAtStart = null; directoryLimit = null; rateLimit = null;
            quota = null;
            Interlocked.Exchange(ref failureTotal, 0);
        }
    }
}
