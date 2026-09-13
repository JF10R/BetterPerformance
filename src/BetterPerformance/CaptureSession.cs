using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using BetterPerformance.Core;

namespace BetterPerformance
{
    internal sealed class CaptureSession
    {
        internal readonly MetricBook Book = new MetricBook();
        internal readonly CaptureWriter Writer;
        internal readonly CaptureClock Clock;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly string OutputPath;
        internal readonly double DurationSeconds;
        internal readonly double IntervalSeconds;
        private double previousExport;
        private long readinessChecks, readinessFalse, probeFailures;

        internal CaptureSession(string directory, string role, double duration, double interval, int capacity,
            long maxBytes, List<TextValue> metadata)
        {
            Clock = new CaptureClock(DateTime.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            DurationSeconds = duration;
            IntervalSeconds = interval;
            OutputPath = Path.Combine(directory, Clock.StartedUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
                + "-" + role + "-" + Id + ".jsonl");
            Writer = new CaptureWriter(() =>
            {
                Directory.CreateDirectory(directory);
                return new FileStream(OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }, capacity, maxBytes);
            metadata.Add(new TextValue("role", role));
            Writer.TryWrite(new CaptureRecord
            {
                Kind = "start", CaptureId = Id, Utc = Clock.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                Labels = metadata.ToArray(),
                Gauges = new[] {
                    new NumberValue("duration_limit", duration, "seconds"),
                    new NumberValue("sample_interval", interval, "seconds"),
                    new NumberValue("queue_capacity", capacity, "records"),
                    new NumberValue("file_size_limit", maxBytes, "bytes"),
                    new NumberValue("logical_processors", Environment.ProcessorCount, "count") }
            });
        }

        internal double Elapsed => Clock.ElapsedSeconds(Stopwatch.GetTimestamp());
        internal bool PollDue => Elapsed - previousExport >= IntervalSeconds;
        internal void RecordProbeFailure() => Interlocked.Increment(ref probeFailures);
        internal void RecordReadiness(bool ready)
        {
            Interlocked.Increment(ref readinessChecks);
            if (!ready) Interlocked.Increment(ref readinessFalse);
        }

        internal void Export(List<NumberValue> gauges, List<TextValue> labels, bool final = false)
        {
            long started = Stopwatch.GetTimestamp();
            double elapsed = Elapsed;
            gauges.Add(new NumberValue("zone_readiness_checks", Interlocked.Exchange(ref readinessChecks, 0), "calls"));
            gauges.Add(new NumberValue("zone_not_ready_results", Interlocked.Exchange(ref readinessFalse, 0), "calls"));
            gauges.Add(new NumberValue("probe_failures_total", Interlocked.Read(ref probeFailures), "count"));
            gauges.Add(new NumberValue("invalid_timing_samples_total", Book.InvalidSamples, "count"));
            gauges.Add(new NumberValue("writer_dropped_records_total", Writer.DroppedRecords, "records"));
            gauges.Add(new NumberValue("writer_bytes_total", Writer.BytesWritten, "bytes"));
            gauges.Add(new NumberValue("writer_last_write", Writer.LastWriteMs, "ms"));
            var record = new CaptureRecord
            {
                Kind = final ? "capture_end" : "interval", CaptureId = Id,
                Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ElapsedSeconds = elapsed, IntervalSeconds = elapsed - previousExport,
                Gauges = gauges.ToArray(), Labels = labels.ToArray(),
                Timings = final ? Book.CloseAndDrain() : Book.Drain()
            };
            previousExport = elapsed;
            Writer.TryWrite(record);
            // Snapshot work is observed in the following interval; the final snapshot has no successor.
            if (!final) Book.Record(Metric.CollectorSnapshot, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            if (final) Writer.Finish(0);
        }
    }
}
