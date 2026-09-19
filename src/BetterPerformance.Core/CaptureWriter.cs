using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace BetterPerformance.Core
{
    // One worker owns all serialization and file I/O. Producers never wait for disk.
    public sealed class CaptureWriter : IDisposable
    {
        private const int FooterReserveBytes = 2048;
        private readonly BlockingCollection<CaptureRecord> queue;
        private readonly object gate = new object();
        private readonly Thread worker;
        private readonly Func<Stream> openStream;
        private readonly long maxBytes;
        private readonly bool leaveOpen;
        // Optional mirror of every encoded line (without its newline), called on the worker
        // thread after the line is on disk. Its failures are counted, never propagated.
        private readonly Action<byte[]>? onLine;
        private long teeFailures;
        private bool accepting = true;
        private bool disposed;
        private long dropped, rejected, written, accepted, bytes, lastWriteTicks;
        private volatile string? lastError;
        private volatile bool limitReached;
        private volatile string priority = "pending";

        public long DroppedRecords => Interlocked.Read(ref dropped);
        public long WrittenRecords => Interlocked.Read(ref written);
        public long AcceptedRecords => Interlocked.Read(ref accepted);
        public long BytesWritten => Interlocked.Read(ref bytes);
        public double LastWriteMs => Interlocked.Read(ref lastWriteTicks) * 1000.0 / Stopwatch.Frequency;
        public string? LastError => lastError;
        public bool LimitReached => limitReached;
        public string Priority => priority;
        public long TeeFailures => Interlocked.Read(ref teeFailures);

        public CaptureWriter(Func<Stream> openStream, int capacity, long maxBytes, bool leaveOpen = false, Action<byte[]>? onLine = null)
        {
            this.onLine = onLine;
            if (capacity < 1 || capacity > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxBytes < FooterReserveBytes * 2) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            this.openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
            this.maxBytes = maxBytes;
            this.leaveOpen = leaveOpen;
            queue = new BlockingCollection<CaptureRecord>(capacity);
            worker = new Thread(WriteLoop) { IsBackground = true, Name = "BetterPerformance capture writer" };
            worker.Start();
        }

        public bool TryWrite(CaptureRecord record)
        {
            lock (gate)
            {
                if (!accepting) return false;
                if (!queue.TryAdd(record)) { Interlocked.Increment(ref rejected); Interlocked.Increment(ref dropped); return false; }
                Interlocked.Increment(ref accepted);
                return true;
            }
        }

        public bool Finish(int timeoutMilliseconds)
        {
            lock (gate)
            {
                accepting = false;
                if (!disposed && !queue.IsAddingCompleted) queue.CompleteAdding();
            }
            return worker.Join(timeoutMilliseconds);
        }

        private void WriteLoop()
        {
            Stream? stream = null;
            string captureId = "";
            try
            {
                // Best effort: file export should yield to normal-priority game work.
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; priority = "below_normal"; }
                catch { priority = "unchanged"; }
                stream = openStream();
                var serializer = new DataContractJsonSerializer(typeof(CaptureRecord));
                foreach (var record in queue.GetConsumingEnumerable())
                {
                    captureId = record.CaptureId;
                    long start = Stopwatch.GetTimestamp();
                    byte[] encoded = Encode(serializer, record);
                    if (BytesWritten + encoded.Length + 1 > maxBytes - FooterReserveBytes)
                    {
                        limitReached = true;
                        Interlocked.Increment(ref dropped);
                        StopAccepting();
                        break;
                    }
                    WriteLine(stream, encoded);
                    stream.Flush();
                    Interlocked.Increment(ref written);
                    Interlocked.Exchange(ref lastWriteTicks, Stopwatch.GetTimestamp() - start);
                }
                StopAccepting();
                while (queue.TryTake(out _)) Interlocked.Increment(ref dropped);
                var footer = new CaptureRecord
                {
                    Kind = "writer_end", CaptureId = captureId,
                    Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Labels = new[] { new TextValue("reason", limitReached ? "file_size_limit" : "completed") },
                    Gauges = new[] {
                        new NumberValue("accepted_records", AcceptedRecords, "records"),
                        new NumberValue("written_records", WrittenRecords, "records"),
                        new NumberValue("dropped_records", DroppedRecords, "records"),
                        new NumberValue("bytes_before_footer", BytesWritten, "bytes"),
                        new NumberValue("last_write_ms", LastWriteMs, "ms") }
                };
                byte[] footerBytes = Encode(serializer, footer);
                if (BytesWritten + footerBytes.Length + 1 <= maxBytes) WriteLine(stream, footerBytes);
                stream.Flush();
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name;
                StopAccepting();
                // Accepted but unwritten records include a record whose serialization/write failed.
                while (queue.TryTake(out _)) { }
                Interlocked.Exchange(ref dropped, Interlocked.Read(ref rejected) + AcceptedRecords - WrittenRecords);
            }
            finally
            {
                if (!leaveOpen && stream != null)
                {
                    try { stream.Dispose(); }
                    catch (Exception exception) { lastError = exception.GetType().Name; }
                }
            }
        }

        private void StopAccepting()
        {
            lock (gate)
            {
                accepting = false;
                if (!queue.IsAddingCompleted) queue.CompleteAdding();
            }
        }

        private static byte[] Encode(DataContractJsonSerializer serializer, CaptureRecord record)
        {
            using (var buffer = new MemoryStream())
            {
                serializer.WriteObject(buffer, record);
                return buffer.ToArray();
            }
        }

        private void WriteLine(Stream stream, byte[] encoded)
        {
            stream.Write(encoded, 0, encoded.Length);
            stream.WriteByte((byte)'\n');
            Interlocked.Add(ref bytes, encoded.Length + 1);
            if (onLine == null) return;
            try { onLine(encoded); }
            catch (Exception) { Interlocked.Increment(ref teeFailures); }
        }

        public void Dispose()
        {
            if (!Finish(2000)) return;
            lock (gate)
            {
                if (disposed) return;
                queue.Dispose();
                disposed = true;
            }
        }
    }
}
