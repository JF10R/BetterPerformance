using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using BetterPerformance.Core;

internal static class Program
{
    private static int Main(string[] args)
    {
        var tests = new Action[] { HistogramBounds, ConcurrentDrain, JsonRoundTrip,
            BoundedQueue, WriterFailure, BoundedFailureAccounting, FileLimit, UniqueFiles, CaptureClock,
            ResidentMemory, MarkerNames, CreationBudgetProgress, CreationBudgetBoundaries,
            AdaptiveCreationQuota, LootPriorityTiers, LootPriorityFailure, LootPriorityFairness,
            CaptureStorageLimit, LootTelemetryTests.Run, ContinuousSegmentExport,
            SlowOperationTests.ThresholdBoundary, SlowOperationTests.FailureBelowThreshold,
            SlowOperationTests.ExclusionsAndBounds, SlowOperationTests.WorkerAndLoopInterpretation,
            SlowOperationTests.IndependentSnapshotsAndValidation, RecorderSampling, CollectorBackoff,
            ConfigurationTests.Run, BudgetTelemetryTests.YieldWaitAndBounds,
            BudgetTelemetryTests.CreationCostsAndBatches, BudgetTelemetryTests.CensoringAndReset,
            AiCadenceTests.Run, MapBitWriterTests.Run, ThreadCpuWindowTests.Run, ExactByteCacheTests.Run,
            MinimapCacheStoreTests.Run,
            PackageCopyTests.Run, ActionTrackerTests.Run, LoadingTimelineTests.Run,
            FrameStepWindowTests.Run, KeyedAggregatorTests.Run, AttributionTargetSplitTests.Run,
            CloudWriteBufferTests.Run, ResendPolicyTests.Run };
        int failures = 0;
        foreach (var test in tests)
        {
            try { test(); Console.WriteLine("PASS " + test.Method.Name); }
            catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + exception); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        if (failures == 0 && args.Length == 2 && args[0] == "--fixture-output") WriteFixture(args[1]);
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RecorderSampling()
    {
        var book = new MetricBook();
        for (int i = 0; i < 65; i++) book.Record(Metric.ObjectCreate, i, i == 64);
        var first = book.Drain();
        var objects = first.Single(x => x.Name == "ObjectCreate");
        Check(objects.Count == 65 && objects.SumMs == 2080 && objects.MaxMs == 64 && objects.FailedCalls == 1,
            "Sampling self-overhead must not sample or lose actual game durations/failures.");
        Check(first.Single(x => x.Name == "TimingRecorder").Count == 1, "Self-overhead uses one in 64 records.");
        for (int i = 0; i < 63; i++) book.Record(Metric.ObjectCreate, 1);
        Check(book.Drain().Single(x => x.Name == "TimingRecorder").Count == 1, "Recorder cadence must cross interval boundaries.");
        for (int i = 0; i < 10000; i++) book.Record(Metric.ObjectCreate, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) book.Record(Metric.ObjectCreate, 1);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Hot aggregation must allocate no managed bytes after warmup.");
        book.CloseAndDrain();
        book.Record(Metric.ObjectCreate, 100);
        Check(book.Drain().All(x => x.Count == 0), "Closed collectors must reject both game and self samples.");
    }

    private static void CollectorBackoff()
    {
        var cadence = new CollectorCadence(2);
        cadence.Observe(2);
        Check(cadence.IntervalSeconds == 2 && cadence.Overruns == 0, "Exact soft budget does not trigger backoff.");
        cadence.Observe(2.01);
        Check(cadence.IntervalSeconds == 4 && cadence.Overruns == 1, "Costly polls must space subsequent snapshots.");
        cadence.Observe(10); cadence.Observe(10); cadence.Observe(10);
        Check(cadence.IntervalSeconds == 10, "Backoff remains bounded so whole-session collection continues.");
        for (int i = 0; i < 29; i++) cadence.Observe(0.1);
        Check(cadence.IntervalSeconds == 10, "A brief recovery must not immediately remove backoff.");
        cadence.Observe(0.1);
        Check(cadence.IntervalSeconds == 5, "Sustained cheap polling recovers gradually.");
        for (int i = 0; i < 90; i++) cadence.Observe(0.1);
        Check(cadence.IntervalSeconds == 2 && cadence.LastCostMs == 0.1 && cadence.Overruns == 4,
            "Recovery respects configured minimum and preserves overrun evidence.");
    }

    private static void CaptureStorageLimit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BetterPerformance-storage-" + Guid.NewGuid().ToString("N"));
        string capture = Path.Combine(directory, "previous.jsonl");
        string unrelated = Path.Combine(directory, "notes.txt");
        try
        {
            Check(CaptureStorage.HasCapacity(directory, 4096, 8192), "A new capture directory should have capacity.");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(capture, new byte[4096]);
            File.WriteAllBytes(unrelated, new byte[16384]);
            Check(CaptureStorage.HasCapacity(directory, 4096, 8192), "Exact remaining JSONL allowance should fit.");
            Check(!CaptureStorage.HasCapacity(directory, 4097, 8192), "Do not start a file that could exceed the directory budget.");
            Check(!CaptureStorage.HasCapacity(directory, 8193, 8192), "Oversized file allowance must be rejected.");
            Check(new FileInfo(capture).Length == 4096 && new FileInfo(unrelated).Length == 16384,
                "Storage checks must not alter or delete existing files.");
        }
        finally
        {
            if (File.Exists(capture)) File.Delete(capture);
            if (File.Exists(unrelated)) File.Delete(unrelated);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static void ContinuousSegmentExport()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BetterPerformance-segments-" + Guid.NewGuid().ToString("N"));
        string recording = Guid.NewGuid().ToString("N");
        var paths = new List<string>();
        try
        {
            for (int segment = 1; segment <= 2; segment++)
            {
                var session = new BetterPerformance.CaptureSession(directory, "client", 1800, 2, 16, 131072,
                    new List<TextValue> { new TextValue("recording_session_id", recording),
                        new TextValue("segment_index", segment.ToString(CultureInfo.InvariantCulture)) });
                paths.Add(session.OutputPath);
                session.Configurations.Observe("graphics.active.SimulationDistance", "3", 0, "initial");
                session.Book.Record(Metric.RpcUpdate, 80);
                session.Export(new List<NumberValue> { new NumberValue("peer_count", 1, "peers") }, new List<TextValue>());
                session.Book.Record(Metric.SaveWorker, 300);
                session.Configurations.Observe("graphics.active.SimulationDistance", "4", 0.1, "graphics_applied");
                session.Export(new List<NumberValue>(), new List<TextValue> { new TextValue("reason", "duration_limit") }, final: true);
                Check(session.Writer.Finish(5000), "Each segment must finish its bounded writer.");
                Check(session.Writer.DroppedRecords == 0 && session.Writer.LastError == null, "Segment export must be complete.");
                session.Writer.Dispose();
                var records = File.ReadAllLines(session.OutputPath).Select(line => JsonDocument.Parse(line)).ToArray();
                try
                {
                    Check(records.Length == 4 && records[3].RootElement.GetProperty("kind").GetString() == "writer_end", "Segment completion footer required.");
                    Check(records[0].RootElement.GetProperty("labels")[0].GetProperty("value").GetString() == recording, "Segments retain recording identity.");
                    Check(records[1].RootElement.GetProperty("slowOperations")[0].GetProperty("operation").GetString() == "RpcUpdate", "Slow alert remains beside interval context.");
                    Check(records[2].RootElement.GetProperty("slowOperations")[0].GetProperty("operation").GetString() == "SaveWorker", "Final interval must preserve worker alert.");
                    var changes = records[2].RootElement.GetProperty("configurationChanges");
                    Check(changes.GetArrayLength() == 1 && changes[0].GetProperty("previous").GetString() == "3"
                        && changes[0].GetProperty("current").GetString() == "4", "Final export preserves pending setting transitions.");
                    Check(records[1].RootElement.GetProperty("configurationChanges").GetArrayLength() == 0,
                        "Each segment starts with a baseline rather than a false setting change.");
                }
                finally { foreach (var record in records) record.Dispose(); }
            }
            Check(paths[0] != paths[1] && File.Exists(paths[0]), "Rolling segments preserve earlier files.");
        }
        finally
        {
            foreach (string path in paths) if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static void CreationBudgetProgress()
    {
        var budget = new CreationBudget(100, 4);
        Check(budget.AllowNext(true, 200), "Readiness skips must not consume the minimum progress allowance.");
        budget.RecordCreation(false);
        Check(budget.AllowNext(true, 200), "A retained invalid prefab must not starve later valid objects.");
        budget.RecordCreation(true);
        Check(!budget.AllowNext(true, 200), "Stop after a successful creation exhausts the budget.");
        Check(!budget.AllowNext(false, 100), "Never manufacture another enumeration element.");
        Check(budget.Attempts == 2 && budget.Successes == 1, "Record attempts independently of success.");
    }

    private static void CreationBudgetBoundaries()
    {
        var budget = new CreationBudget(100, 4);
        budget.RecordCreation(true);
        Check(budget.AllowNext(true, 103), "Allow work before the deadline.");
        Check(!budget.AllowNext(true, 104), "Yield at the exact deadline.");
        var nextBatch = new CreationBudget(200, 4);
        Check(nextBatch.AllowNext(true, 300), "Each batch must regain minimum progress.");
        Check(nextBatch.Attempts == 0, "Attempts must not leak between batches.");
    }

    private static void HistogramBounds()
    {
        var book = new MetricBook();
        for (int i = 1; i <= 100; i++) book.Record(Metric.LoopInterval, i, i == 100);
        book.Record(Metric.LoopInterval, double.NaN);
        book.Record(Metric.LoopInterval, -1);
        book.Record(Metric.LoopInterval, double.PositiveInfinity);
        var result = book.Drain().Single(x => x.Name == "LoopInterval");
        Check(result.Count == 100 && result.FailedCalls == 1, "Count or failed-call count is wrong.");
        Check(result.SumMs == 5050 && result.MaxMs == 100, "Exact aggregates are wrong.");
        Check(result.P95UpperBoundMs >= 95 && result.P95UpperBoundMs <= 128, "Percentile must bound the sample percentile.");
        Check(result.StallsOver50Ms == 50, "Stall threshold must be strictly greater than 50 ms.");
        Check(book.InvalidSamples == 3, "Invalid samples must be visible.");
        Check(book.Drain().All(x => x.Count == 0), "Drain must reset the interval.");
        book.Record(Metric.LoopInterval, 1000000);
        Check(book.Drain().Single(x => x.Name == "LoopInterval").P99UpperBoundMs == 1000000,
            "Overflow bucket must use the actual maximum, not an undersized upper bound.");
    }

    private static void AdaptiveCreationQuota()
    {
        Check(CreationScheduling.ExpandQuota(10, 64, false) == 10, "Disabled quota must retain vanilla behavior.");
        Check(CreationScheduling.ExpandQuota(10, 64, true) == 64, "Cheap batches can use a larger allowance.");
        Check(CreationScheduling.ExpandQuota(100, 64, true) == 100, "Do not reduce loading-screen allowance.");
        var budget = new CreationBudget(0, 4);
        budget.RecordCreation(true);
        Check(!budget.AllowNext(true, 4), "Extra count allowance must not bypass the time budget.");
    }

    private static void LootPriorityTiers()
    {
        var items = new List<(int Tier, string Name, bool Loot)>
        {
            (3, "terrain", false), (2, "solid", false),
            (1, "actor", false), (1, "near_loot_a", true), (1, "near_loot_b", true),
            (0, "far_loot", false), (0, "decoration", false), (0, "near_loot_c", true)
        };
        var scratch = new List<(int Tier, string Name, bool Loot)>();
        int prioritized = CreationScheduling.PrioritizeWithinTiers(items, scratch, item => item.Tier, item => item.Loot);
        Check(prioritized == 3, "Count selected candidates, not created objects.");
        Check(items.Select(item => item.Name).SequenceEqual(new[] { "terrain", "solid", "near_loot_a", "near_loot_b", "actor", "near_loot_c", "far_loot", "decoration" }),
            "Preserve tier boundaries and stable order within priority and ordinary groups.");
        Check(scratch.Count == 0, "Scratch must not retain object references.");
    }

    private static void LootPriorityFailure()
    {
        var items = new List<int> { 1, 2, 3 };
        var scratch = new List<int>();
        bool failed = false;
        try { CreationScheduling.PrioritizeWithinTiers(items, scratch, _ => 0, value => value == 3 ? throw new InvalidOperationException("fixture") : value == 2); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && items.SequenceEqual(new[] { 1, 2, 3 }), "Classification failure must preserve the entire vanilla order.");
        Check(scratch.Count == 0, "Failure must release scratch references.");
    }

    private static void LootPriorityFairness()
    {
        int turns = 0;
        for (int i = 0; i < 12; i++)
            Check(CreationScheduling.TakePriorityTurn(ref turns) == (i % 4 != 3), "Every fourth eligible pass must preserve vanilla order.");
    }

    private static void ConcurrentDrain()
    {
        var book = new MetricBook();
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < 10000; i++) book.Record(Metric.SaveWorker, 1);
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        long total = 0;
        while (threads.Any(x => x.IsAlive)) total += book.Drain().Single(x => x.Name == "SaveWorker").Count;
        foreach (var thread in threads) thread.Join();
        total += book.CloseAndDrain().Single(x => x.Name == "SaveWorker").Count;
        book.Record(Metric.SaveWorker, 1);
        Check(total == 40000, "Concurrent interval rotation lost or duplicated samples.");
        Check(book.Drain().Single(x => x.Name == "SaveWorker").Count == 0, "Closed captures must reject late results.");
    }

    private static CaptureRecord Sample(string note = "test") => new CaptureRecord
    {
        Kind = "interval", CaptureId = "test", Utc = "2026-01-01T00:00:00.0000000Z",
        ElapsedSeconds = 1.5, IntervalSeconds = 1.0,
        Labels = new[] { new TextValue("note", note) },
        Gauges = new[] { new NumberValue("value", 1.25, "ms") }
    };

    private static void JsonRoundTrip()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
            using var stream = new MemoryStream();
            using var writer = new CaptureWriter(() => stream, 4, 1024 * 1024, leaveOpen: true);
            Check(writer.TryWrite(Sample("quote\"\nUnicode é")), "Record rejected.");
            Check(writer.Finish(5000), "Writer failed to finish.");
            var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length == 2, "Expected interval plus completion record.");
            using var record = JsonDocument.Parse(lines[0]);
            Check(record.RootElement.GetProperty("elapsedSeconds").GetDouble() == 1.5, "Numbers must be culture invariant.");
            Check(record.RootElement.GetProperty("labels")[0].GetProperty("value").GetString() == "quote\"\nUnicode é", "String escaping failed.");
            using var footer = JsonDocument.Parse(lines[1]);
            Check(footer.RootElement.GetProperty("kind").GetString() == "writer_end", "Completion record missing.");
            Check(writer.WrittenRecords == 1 && writer.LastError == null, "Writer counts incorrect.");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static void BoundedQueue()
    {
        using var stream = new GatedStream();
        using var writer = new CaptureWriter(() => stream, 2, 1024 * 1024, leaveOpen: true);
        try
        {
            Check(writer.TryWrite(Sample()), "First record rejected.");
            Check(stream.Entered.Wait(5000), "Writer did not enter stream.");
            Check(writer.TryWrite(Sample()) && writer.TryWrite(Sample()), "Queue capacity incorrect.");
            Check(!writer.TryWrite(Sample()) && writer.DroppedRecords == 1, "Full queue must drop without blocking.");
        }
        finally { stream.Release.Set(); }
        Check(writer.Finish(5000), "Queue failed to drain.");
        Check(writer.WrittenRecords == 3 && !writer.TryWrite(Sample()), "Completion must reject further writes.");
    }

    private static void WriterFailure()
    {
        using var writer = new CaptureWriter(() => throw new IOException("test failure"), 2, 1024 * 1024);
        writer.TryWrite(Sample());
        Check(writer.Finish(5000), "Failed writer thread did not terminate.");
        Check(writer.LastError == nameof(IOException), "I/O failures must be exposed without payloads or paths.");
        Check(!writer.TryWrite(Sample()), "Failed writer must reject more data.");
    }

    private static void BoundedFailureAccounting()
    {
        using var stream = new GatedStream { FailWrites = true };
        using var writer = new CaptureWriter(() => stream, 2, 4096, leaveOpen: true);
        try
        {
            writer.TryWrite(Sample());
            Check(stream.Entered.Wait(5000), "Writer did not enter stream.");
            writer.TryWrite(Sample());
            writer.TryWrite(Sample());
            Check(!writer.TryWrite(Sample()), "Full queue should reject record.");
        }
        finally { stream.Release.Set(); }
        Check(writer.Finish(5000), "Failed writer did not exit.");
        Check(writer.DroppedRecords == 4 && writer.WrittenRecords == 0,
            "Failure must account for rejected, queued, and partially written records.");
    }

    private static void WriteFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new CaptureWriter(() => new FileStream(path, FileMode.CreateNew, FileAccess.Write), 8, 1024 * 1024);
        var start = Sample();
        start.Kind = "start";
        start.Labels = new[] { new TextValue("role", "synthetic_client"), new TextValue("synthetic", "true"),
            new TextValue("recording_session_id", "synthetic-session"), new TextValue("segment_index", "1") };
        writer.TryWrite(start);
        var book = new MetricBook();
        book.Record(Metric.LoopInterval, 80);
        book.Record(Metric.RpcUpdate, 60);
        var interval = Sample();
        interval.Timings = book.Drain();
        interval.SlowOperations = SlowOperationSummary.Create(interval.Timings);
        writer.TryWrite(interval);
        var end = Sample();
        end.Kind = "capture_end";
        writer.TryWrite(end);
        Check(writer.Finish(5000) && writer.LastError == null, "Fixture export failed.");
    }

    private static void FileLimit()
    {
        using var stream = new MemoryStream();
        using var writer = new CaptureWriter(() => stream, 16, 4096, leaveOpen: true);
        for (int i = 0; i < 16; i++) writer.TryWrite(Sample(new string('x', 600)));
        Check(writer.Finish(5000), "Size-limited writer did not terminate.");
        Check(writer.LimitReached && stream.Length <= 4096, "Output must remain within the byte limit.");
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
        using var footer = JsonDocument.Parse(lines.Last());
        Check(footer.RootElement.GetProperty("kind").GetString() == "writer_end", "Reserve space for the completion record.");
        Check(writer.DroppedRecords > 0, "Discarded records at the file limit must be counted.");
    }

    private static void UniqueFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BetterPerformance-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "capture.jsonl");
        try
        {
            File.WriteAllText(path, "existing");
            using var writer = new CaptureWriter(() => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), 2, 4096);
            writer.TryWrite(Sample());
            Check(writer.Finish(5000) && writer.LastError != null, "Existing files must not be overwritten.");
            Check(File.ReadAllText(path) == "existing", "Existing contents changed.");
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private static void CaptureClock()
    {
        var clock = new CaptureClock(DateTime.UtcNow, 100, 10);
        Check(clock.ElapsedSeconds(125) == 2.5, "Monotonic conversion incorrect.");
        Check(clock.ElapsedSeconds(90) == 0, "Negative elapsed time must not leak into records.");
    }

    private static void ResidentMemory()
    {
        Check(ProcessMemory.TryRead(out long bytes, out string source), "Resident memory should be readable on the test host.");
        Check(bytes > 0 && source != "unavailable", "Zero must never be exported as valid resident memory.");
    }

    private static void MarkerNames()
    {
        Check(CaptureMarkers.IsValid("traversal_128m"), "Scenario identifier rejected.");
        Check(CaptureMarkers.CrossesBoundary(10, 600, 590), "A pre-marker stall must be marked as crossing, not attributed entirely to the next phase.");
        Check(!CaptureMarkers.CrossesBoundary(600, 630, 590), "A later loop must not inherit the marker.");
        Check(!CaptureMarkers.CrossesBoundary(10, 20, 30), "A future marker cannot affect an earlier loop.");
        foreach (string name in new[] { "", "user name", "line\nbreak", new string('a', 49), "a|b" })
            Check(!CaptureMarkers.IsValid(name), "Unbounded or free-text marker accepted.");
    }

    private sealed class GatedStream : MemoryStream
    {
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        public bool FailWrites { get; set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Entered.Set();
            if (!Release.Wait(5000)) throw new TimeoutException("Test stream gate timed out.");
            if (FailWrites) throw new IOException("Synthetic write failure.");
            base.Write(buffer, offset, count);
        }
    }
}
