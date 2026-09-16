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
        internal readonly ConfigurationTracker Configurations = new ConfigurationTracker();
        internal readonly CaptureWriter Writer;
        internal readonly CaptureClock Clock;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly string OutputPath;
        internal readonly double DurationSeconds;
        internal readonly double IntervalSeconds;
        internal readonly CollectorCadence Cadence;
        private readonly bool slowOperations;
        private readonly double slowMethodMs, slowLoopMs, slowWorkerMs;
        private double previousExport;
        private long readinessChecks, readinessFalse, probeFailures;
        internal string Phase = "unmarked";
        internal int MarkerCount;
        internal long LastMarkerTimestamp;

        internal void Mark(string name)
        {
            Phase = name;
            LastMarkerTimestamp = Stopwatch.GetTimestamp();
            MarkerCount++;
            Writer.TryWrite(new CaptureRecord {
                Kind = "marker", CaptureId = Id, Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ElapsedSeconds = Elapsed, Labels = new[] { new TextValue("phase", name) }
            });
        }

        internal CaptureSession(string directory, string role, double duration, double interval, int capacity,
            long maxBytes, List<TextValue> metadata, bool slowOperations = true,
            double slowMethodMs = 20, double slowLoopMs = 100, double slowWorkerMs = 250,
            List<NumberValue>? startGauges = null)
        {
            Clock = new CaptureClock(DateTime.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            DurationSeconds = duration;
            IntervalSeconds = interval;
            Cadence = new CollectorCadence(interval);
            this.slowOperations = slowOperations;
            this.slowMethodMs = slowMethodMs;
            this.slowLoopMs = slowLoopMs;
            this.slowWorkerMs = slowWorkerMs;
            OutputPath = Path.Combine(directory, Clock.StartedUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
                + "-" + role + "-" + Id + ".jsonl");
            Writer = new CaptureWriter(() =>
            {
                Directory.CreateDirectory(directory);
                return new FileStream(OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }, capacity, maxBytes);
            metadata.Add(new TextValue("role", role));
            var gauges = new List<NumberValue> {
                    new NumberValue("duration_limit", duration, "seconds"),
                    new NumberValue("sample_interval", interval, "seconds"),
                    new NumberValue("queue_capacity", capacity, "records"),
                    new NumberValue("file_size_limit", maxBytes, "bytes"),
                    new NumberValue("logical_processors", Environment.ProcessorCount, "count"),
                    new NumberValue("slow_operations_enabled", slowOperations ? 1 : 0, "boolean"),
                    new NumberValue("slow_method_threshold", slowMethodMs, "ms"),
                    new NumberValue("slow_loop_threshold", slowLoopMs, "ms"),
                    new NumberValue("slow_save_worker_threshold", slowWorkerMs, "ms") };
            // Start-only host facts (priority, affinity, timer resolution, shared counter) join the start record.
            if (startGauges != null) gauges.AddRange(startGauges);
            Writer.TryWrite(new CaptureRecord
            {
                Kind = "start", CaptureId = Id, Utc = Clock.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                Labels = metadata.ToArray(),
                Gauges = gauges.ToArray()
            });
        }

        internal double Elapsed => Clock.ElapsedSeconds(Stopwatch.GetTimestamp());
        internal bool PollDue => Elapsed - previousExport >= Cadence.IntervalSeconds;
        internal void RecordProbeFailure() => Interlocked.Increment(ref probeFailures);
        internal void RecordReadiness(bool ready)
        {
            Interlocked.Increment(ref readinessChecks);
            if (!ready) Interlocked.Increment(ref readinessFalse);
        }

        internal void Export(List<NumberValue> gauges, List<TextValue> labels, bool final = false,
            AttributionSummary[]? attributions = null)
        {
            long started = Stopwatch.GetTimestamp();
            double elapsed = Elapsed;
            labels.Add(new TextValue("phase", Phase));
            gauges.Add(new NumberValue("zone_readiness_checks", Interlocked.Exchange(ref readinessChecks, 0), "calls"));
            gauges.Add(new NumberValue("zone_not_ready_results", Interlocked.Exchange(ref readinessFalse, 0), "calls"));
            gauges.Add(new NumberValue("probe_failures_total", Interlocked.Read(ref probeFailures), "count"));
            gauges.Add(new NumberValue("invalid_timing_samples_total", Book.InvalidSamples, "count"));
            gauges.Add(new NumberValue("writer_dropped_records_total", Writer.DroppedRecords, "records"));
            gauges.Add(new NumberValue("writer_bytes_total", Writer.BytesWritten, "bytes"));
            gauges.Add(new NumberValue("writer_last_write", Writer.LastWriteMs, "ms"));
            gauges.Add(new NumberValue("collector_target_interval_at_poll", Cadence.IntervalSeconds, "seconds"));
            gauges.Add(new NumberValue("collector_previous_poll_cost", Cadence.LastCostMs, "ms"));
            gauges.Add(new NumberValue("collector_poll_overruns_total", Cadence.Overruns, "polls"));
            labels.Add(new TextValue("writer_priority", Writer.Priority));
            Configurations.AppendSnapshot(labels);
            gauges.Add(new NumberValue("configuration_changes_dropped_total", Configurations.DroppedChanges, "field_changes"));
            gauges.Add(new NumberValue("configuration_keys_rejected_total", Configurations.RejectedKeys, "observations"));
            var record = new CaptureRecord
            {
                Kind = final ? "capture_end" : "interval", CaptureId = Id,
                Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ElapsedSeconds = elapsed, IntervalSeconds = elapsed - previousExport,
                Gauges = gauges.ToArray(), Labels = labels.ToArray(),
                // Idle metrics carry no information; omitting them keeps interval records small
                // as the probe set grows. Readers treat an absent name as zero calls.
                Timings = Array.FindAll(final ? Book.CloseAndDrain() : Book.Drain(), t => t.Count > 0),
                ConfigurationChanges = Configurations.Drain(),
                Attributions = attributions ?? Array.Empty<AttributionSummary>()
            };
            if (slowOperations) record.SlowOperations = SlowOperationSummary.Create(record.Timings, slowMethodMs, slowLoopMs, slowWorkerMs);
            previousExport = elapsed;
            Writer.TryWrite(record);
            // Snapshot work is observed in the following interval; the final snapshot has no successor.
            if (!final) Book.Record(Metric.CollectorSnapshot, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            if (final) Writer.Finish(0);
        }
    }
}
