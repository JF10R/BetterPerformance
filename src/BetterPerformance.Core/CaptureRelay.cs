using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterPerformance.Core
{
    // Wire protocol for mirroring a client's capture (and, when opted in, its BepInEx log) to the
    // server it plays on. The plugin owns the transport (one ZPackage per chunk over the peer's
    // ZRpc); everything here is pure and offline-testable: how a message is framed and split, how
    // a receiver validates, reassembles and stores it, and what bounds apply on both sides.
    public enum RelayStreamKind : byte { Capture = 1, Log = 2 }

    // One transport unit. A message (one JSONL record, or one batch of log lines) is framed with
    // CompressionFrame and split into chunks of at most the outbox's chunk size; Final marks the
    // last piece of a message, End closes the stream (the peer stopped writing that file).
    public sealed class RelayChunk
    {
        public string Name = "";
        public RelayStreamKind Kind;
        public int Sequence;
        public bool Final;
        public bool End;
        public byte[] Payload = Array.Empty<byte>();
    }

    public static class RelayNames
    {
        // UTC start, role, 32-hex id, optional reconnect suffix, extension by kind. The name is
        // the file name the receiver writes, so nothing outside this shape is ever accepted.
        private static readonly Regex Shape = new Regex(
            @"^[0-9]{8}T[0-9]{9}Z-[a-z_]{1,32}-(?<id>[0-9a-f]{32})(-r[0-9]{1,4})?\.(jsonl|log)$", RegexOptions.CultureInvariant);
        public const int MaxLength = 96;

        // The 32-hex id inside a valid name, empty otherwise.
        public static string Id(string? name)
        {
            if (name == null || name.Length > MaxLength) return "";
            var match = Shape.Match(name);
            return match.Success ? match.Groups["id"].Value : "";
        }

        public static bool IsValid(string? name, RelayStreamKind kind)
        {
            if (name == null || name.Length > MaxLength || !Shape.IsMatch(name)) return false;
            return kind == RelayStreamKind.Capture ? name.EndsWith(".jsonl", StringComparison.Ordinal)
                : kind == RelayStreamKind.Log && name.EndsWith(".log", StringComparison.Ordinal);
        }
    }

    public struct RelayOutboxSummary
    {
        public long Messages, MessageBytes, DroppedMessages, DroppedBytes, ChunksSent, BytesSent, PendingBytes, PendingChunks;
    }

    // Sender side. Producers (the capture writer thread, the log listener) enqueue whole messages;
    // the game thread dequeues one chunk at a time when the socket is idle. Bounded by pending
    // bytes: a message that does not fit is dropped whole and counted, never partially queued.
    public sealed class RelayOutbox
    {
        public const int DefaultChunkBytes = 8192;
        private readonly object gate = new object();
        private readonly Queue<RelayChunk> chunks = new Queue<RelayChunk>();
        private readonly Dictionary<string, int> sequences = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly long maxPendingBytes;
        private readonly int chunkBytes;
        private long pendingBytes;
        private long messages, messageBytes, droppedMessages, droppedBytes, chunksSent, bytesSent;

        public RelayOutbox(long maxPendingBytes, int chunkBytes = DefaultChunkBytes)
        {
            if (maxPendingBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxPendingBytes));
            if (chunkBytes < 64 || chunkBytes > CompressionFrame.MaxRawLength) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            this.maxPendingBytes = maxPendingBytes;
            this.chunkBytes = chunkBytes;
        }

        public long PendingBytes { get { lock (gate) return pendingBytes; } }
        public int PendingChunks { get { lock (gate) return chunks.Count; } }

        // Frames the message, splits it, and queues every piece or nothing.
        public bool Enqueue(string name, RelayStreamKind kind, byte[] message)
        {
            if (message == null || message.Length == 0 || !RelayNames.IsValid(name, kind)) return false;
            byte[] framed = message.Length <= CompressionFrame.MaxRawLength ? CompressionFrame.Encode(message) : message;
            lock (gate)
            {
                if (pendingBytes + framed.Length > maxPendingBytes)
                {
                    droppedMessages++;
                    droppedBytes += message.Length;
                    return false;
                }
                sequences.TryGetValue(name, out int sequence);
                for (int offset = 0; offset < framed.Length; offset += chunkBytes)
                {
                    int length = Math.Min(chunkBytes, framed.Length - offset);
                    var payload = new byte[length];
                    Buffer.BlockCopy(framed, offset, payload, 0, length);
                    chunks.Enqueue(new RelayChunk { Name = name, Kind = kind, Sequence = sequence++, Final = offset + length == framed.Length, Payload = payload });
                }
                sequences[name] = sequence;
                pendingBytes += framed.Length;
                messages++;
                messageBytes += message.Length;
                return true;
            }
        }

        // Tells the receiver the file is complete; queued after everything already enqueued.
        public void EndStream(string name, RelayStreamKind kind)
        {
            if (!RelayNames.IsValid(name, kind)) return;
            lock (gate)
            {
                sequences.TryGetValue(name, out int sequence);
                chunks.Enqueue(new RelayChunk { Name = name, Kind = kind, Sequence = sequence, Final = true, End = true });
                sequences.Remove(name);
            }
        }

        public bool TryDequeue(out RelayChunk chunk)
        {
            lock (gate)
            {
                if (chunks.Count == 0) { chunk = null!; return false; }
                chunk = chunks.Dequeue();
                pendingBytes -= chunk.Payload.Length;
                chunksSent++;
                bytesSent += chunk.Payload.Length;
                return true;
            }
        }

        // Drops everything queued and forgets every sequence: the next message of a stream starts
        // at 0 again, so the caller must also rename the stream (a receiver refuses a restart).
        public void Clear()
        {
            lock (gate)
            {
                while (chunks.Count > 0) { var c = chunks.Dequeue(); droppedBytes += c.Payload.Length; }
                pendingBytes = 0;
                sequences.Clear();
            }
        }

        public RelayOutboxSummary Drain()
        {
            lock (gate)
            {
                var summary = new RelayOutboxSummary
                {
                    Messages = messages, MessageBytes = messageBytes, DroppedMessages = droppedMessages, DroppedBytes = droppedBytes,
                    ChunksSent = chunksSent, BytesSent = bytesSent, PendingBytes = pendingBytes, PendingChunks = chunks.Count
                };
                messages = messageBytes = droppedMessages = droppedBytes = chunksSent = bytesSent = 0;
                return summary;
            }
        }
    }

    // Collects log lines into one message so a 100-byte line is not a 100-byte chunk. Flushes on
    // size or age; the buffer is bounded and a line past the bound is dropped and counted.
    public sealed class RelayLineBatcher
    {
        private readonly object gate = new object();
        private readonly StringBuilder buffer = new StringBuilder();
        private readonly int flushBytes, maxBytes;
        private readonly double flushSeconds;
        private double oldest = -1;
        private long droppedLines;

        public RelayLineBatcher(int flushBytes = 8192, double flushSeconds = 1.0, int maxBytes = 262144)
        {
            if (flushBytes < 1 || maxBytes < flushBytes) throw new ArgumentOutOfRangeException(nameof(flushBytes));
            this.flushBytes = flushBytes;
            this.maxBytes = maxBytes;
            this.flushSeconds = flushSeconds;
        }

        public long DroppedLines { get { lock (gate) return droppedLines; } }

        public void Append(string line, double nowSeconds)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (gate)
            {
                if (buffer.Length + line.Length > maxBytes) { droppedLines++; return; }
                if (buffer.Length == 0) oldest = nowSeconds;
                buffer.Append(line);
                if (line[line.Length - 1] != '\n') buffer.Append('\n');
            }
        }

        // Returns the batch when it is large enough, old enough, or forced; otherwise null.
        public byte[]? TryTake(double nowSeconds, bool force = false)
        {
            lock (gate)
            {
                if (buffer.Length == 0) return null;
                if (!force && buffer.Length < flushBytes && nowSeconds - oldest < flushSeconds) return null;
                byte[] bytes = Encoding.UTF8.GetBytes(buffer.ToString());
                buffer.Length = 0;
                oldest = -1;
                return bytes;
            }
        }
    }

    // Bytes the receiver may still write, shared by every sink of a process.
    public sealed class RelayQuota
    {
        private readonly object gate = new object();
        private long used;
        public long Limit { get; }
        public RelayQuota(long limitBytes, long alreadyUsed = 0)
        {
            if (limitBytes < 0 || alreadyUsed < 0) throw new ArgumentOutOfRangeException(nameof(limitBytes));
            Limit = limitBytes;
            used = alreadyUsed;
        }
        public long Used { get { lock (gate) return used; } }
        public bool TryReserve(long bytes)
        {
            if (bytes < 0) return false;
            lock (gate)
            {
                if (used + bytes > Limit) return false;
                used += bytes;
                return true;
            }
        }
    }

    public enum RelayAccept { Written, Opened, Closed, RejectedName, RejectedSequence, RejectedSize, RejectedQuota, RejectedCorrupt, RejectedStreams, Failed }

    public struct RelaySinkSummary
    {
        public long Chunks, BytesReceived, BytesWritten, Messages, Rejected, Failures, StreamsOpened, StreamsClosed, OpenStreams;
    }

    // Receiver side, one per peer. Validates every chunk against the stream's expected sequence,
    // reassembles a message, decodes the frame, and appends it to the stream the factory opened
    // for that name. A stream that breaks (gap, corrupt frame, oversize message, write failure)
    // is closed and refused for the rest of the connection; a later chunk for it is rejected,
    // never re-opened. Nothing is ever written under a name RelayNames refuses.
    public sealed class RelaySink : IDisposable
    {
        public const long DefaultMaxMessageBytes = 4L * 1024 * 1024;
        private static readonly DataContractJsonSerializer TrailerSerializer = new DataContractJsonSerializer(typeof(CaptureRecord));
        private readonly object gate = new object();
        private readonly Func<string, RelayStreamKind, Stream> open;
        private readonly RelayQuota quota;
        private readonly long maxMessageBytes, maxStreamBytes;
        private readonly int maxStreams;
        private readonly Dictionary<string, StreamState> streams = new Dictionary<string, StreamState>(StringComparer.Ordinal);
        private readonly HashSet<string> refused = new HashSet<string>(StringComparer.Ordinal);
        private long chunks, bytesReceived, bytesWritten, messages, rejected, failures, opened, closed;
        private bool disposed;

        private sealed class StreamState
        {
            internal Stream? Stream;
            internal RelayStreamKind Kind;
            internal int NextSequence;
            internal long BytesWritten;
            internal readonly MemoryStream Assembly = new MemoryStream();
        }

        public RelaySink(Func<string, RelayStreamKind, Stream> open, RelayQuota quota,
            long maxMessageBytes = DefaultMaxMessageBytes, long maxStreamBytes = 256L * 1024 * 1024, int maxStreams = 8)
        {
            this.open = open ?? throw new ArgumentNullException(nameof(open));
            this.quota = quota ?? throw new ArgumentNullException(nameof(quota));
            if (maxMessageBytes < 1 || maxStreamBytes < maxMessageBytes || maxStreams < 1) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            this.maxMessageBytes = maxMessageBytes;
            this.maxStreamBytes = maxStreamBytes;
            this.maxStreams = maxStreams;
        }

        public int OpenStreams { get { lock (gate) { int n = 0; foreach (var s in streams.Values) if (s.Stream != null) n++; return n; } } }

        public RelayAccept Accept(RelayChunk chunk)
        {
            if (chunk == null) return Reject(RelayAccept.RejectedName);
            lock (gate)
            {
                if (disposed) return Reject(RelayAccept.Failed);
                chunks++;
                bytesReceived += chunk.Payload?.Length ?? 0;
                if (!RelayNames.IsValid(chunk.Name, chunk.Kind) || refused.Contains(chunk.Name)) return Reject(RelayAccept.RejectedName);
                if (!streams.TryGetValue(chunk.Name, out var state))
                {
                    // A stream starts at 0; a peer resuming mid-file (reconnect) is refused so no
                    // file ever begins with the tail of a message.
                    if (chunk.Sequence != 0) return Refuse(chunk.Name, RelayAccept.RejectedSequence);
                    if (streams.Count >= maxStreams) return Refuse(chunk.Name, RelayAccept.RejectedStreams);
                    state = new StreamState { Kind = chunk.Kind };
                    try { state.Stream = open(chunk.Name, chunk.Kind); }
                    catch (Exception) { failures++; return Refuse(chunk.Name, RelayAccept.Failed); }
                    if (state.Stream == null) { failures++; return Refuse(chunk.Name, RelayAccept.Failed); }
                    streams[chunk.Name] = state;
                    opened++;
                    if (chunk.End) return Close(chunk.Name, state, RelayAccept.Closed);
                    return Append(chunk, state, RelayAccept.Opened);
                }
                if (chunk.Sequence != state.NextSequence) return Break(chunk.Name, state, RelayAccept.RejectedSequence);
                if (chunk.End) return Close(chunk.Name, state, RelayAccept.Closed);
                return Append(chunk, state, RelayAccept.Written);
            }
        }

        private RelayAccept Append(RelayChunk chunk, StreamState state, RelayAccept success)
        {
            state.NextSequence = chunk.Sequence + 1;
            byte[] payload = chunk.Payload ?? Array.Empty<byte>();
            if (state.Assembly.Length + payload.Length > maxMessageBytes) return Break(chunk.Name, state, RelayAccept.RejectedSize);
            state.Assembly.Write(payload, 0, payload.Length);
            if (!chunk.Final) return success;
            byte[] framed = state.Assembly.ToArray();
            state.Assembly.SetLength(0);
            byte[] raw;
            if (CompressionFrame.IsFramed(framed)) { if (!CompressionFrame.TryDecode(framed, out raw)) return Break(chunk.Name, state, RelayAccept.RejectedCorrupt); }
            else raw = framed;
            if (raw.Length == 0) return success;
            int newline = state.Kind == RelayStreamKind.Capture && raw[raw.Length - 1] != (byte)'\n' ? 1 : 0;
            long size = raw.Length + newline;
            if (state.BytesWritten + size > maxStreamBytes) return Break(chunk.Name, state, RelayAccept.RejectedSize);
            if (!quota.TryReserve(size)) return Break(chunk.Name, state, RelayAccept.RejectedQuota);
            try
            {
                state.Stream!.Write(raw, 0, raw.Length);
                if (newline == 1) state.Stream.WriteByte((byte)'\n');
                state.Stream.Flush();
            }
            catch (Exception) { failures++; return Break(chunk.Name, state, RelayAccept.Failed); }
            state.BytesWritten += size;
            bytesWritten += size;
            messages++;
            return success;
        }

        private RelayAccept Reject(RelayAccept reason) { rejected++; return reason; }

        private RelayAccept Refuse(string name, RelayAccept reason) { refused.Add(name); return Reject(reason); }

        // The stream is unusable from here: close it with a trailer naming the reason and refuse its name.
        private RelayAccept Break(string name, StreamState state, RelayAccept reason)
        {
            CloseStream(name, state, reason.ToString());
            refused.Add(name);
            return Reject(reason);
        }

        private RelayAccept Close(string name, StreamState state, RelayAccept result)
        {
            CloseStream(name, state, null);
            refused.Add(name);
            return result;
        }

        private void CloseStream(string name, StreamState state, string? trailerReason)
        {
            streams.Remove(name);
            closed++;
            var stream = state.Stream;
            state.Stream = null;
            if (stream == null) return;
            try
            {
                if (trailerReason != null) WriteTrailer(stream, name, state.Kind, trailerReason);
                stream.Flush();
            }
            catch (Exception) { failures++; }
            finally { try { stream.Dispose(); } catch (Exception) { failures++; } }
        }

        // A capture that ends without its writer_end gets a relay_end record so summarize_capture
        // reports the reason instead of a bare "completion record missing".
        private void WriteTrailer(Stream stream, string name, RelayStreamKind kind, string reason)
        {
            byte[] bytes;
            if (kind == RelayStreamKind.Capture)
            {
                var record = new CaptureRecord
                {
                    Kind = "relay_end", CaptureId = RelayNames.Id(name), Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Labels = new[] { new TextValue("reason", reason) }
                };
                using (var buffer = new MemoryStream()) { TrailerSerializer.WriteObject(buffer, record); buffer.WriteByte((byte)'\n'); bytes = buffer.ToArray(); }
            }
            else bytes = Encoding.UTF8.GetBytes("[relay_end reason=" + reason + "]\n");
            if (!quota.TryReserve(bytes.Length)) return;
            stream.Write(bytes, 0, bytes.Length);
            bytesWritten += bytes.Length;
        }

        // The peer went away: every open stream gets a trailer with the reason and is closed.
        public void CloseAll(string reason)
        {
            lock (gate)
            {
                var names = new List<string>(streams.Keys);
                foreach (string name in names) CloseStream(name, streams[name], reason);
            }
        }

        public RelaySinkSummary Drain()
        {
            lock (gate)
            {
                var summary = new RelaySinkSummary
                {
                    Chunks = chunks, BytesReceived = bytesReceived, BytesWritten = bytesWritten, Messages = messages, Rejected = rejected,
                    Failures = failures, StreamsOpened = opened, StreamsClosed = closed, OpenStreams = streams.Count
                };
                chunks = bytesReceived = bytesWritten = messages = rejected = failures = opened = closed = 0;
                return summary;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                CloseAll("sink_disposed");
                disposed = true;
            }
        }
    }
}
