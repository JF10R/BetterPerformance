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
            BoundedQueue, WriterFailure, BoundedFailureAccounting, FileLimit, UniqueFiles, CaptureClock };
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
        start.Labels = new[] { new TextValue("role", "synthetic_client"), new TextValue("synthetic", "true") };
        writer.TryWrite(start);
        var book = new MetricBook();
        book.Record(Metric.LoopInterval, 80);
        var interval = Sample();
        interval.Timings = book.Drain();
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
