using System;
using System.Linq;
using BetterPerformance.Core;

internal static class SlowOperationTests
{
    public static void ThresholdBoundary()
    {
        var timings = new[] { Sample(Metric.ObjectCreate, 19.999), Sample(Metric.NetworkUpdate, 20) };
        var summaries = SlowOperationSummary.Create(timings);
        Check(summaries.Length == 1 && summaries[0].Operation == "NetworkUpdate", "Use an inclusive threshold.");
        Check(summaries[0].ExceedingMax && summaries[0].ThresholdMs == 20, "Record the threshold that triggered the summary.");
        Check(summaries[0].TotalCalls == 4 && summaries[0].StallsOver50Ms == 0, "TotalCalls is the entire interval count, not a slow-call count.");
        Check(SlowOperationSummary.Create(new[] { Sample(Metric.NetworkUpdate, 29) }, 30, 100, 250).Length == 0,
            "Respect the configured method threshold.");
    }

    public static void FailureBelowThreshold()
    {
        var timing = Sample(Metric.RpcUpdate, 1);
        timing.FailedCalls = 1;
        var summaries = SlowOperationSummary.Create(new[] { timing });
        Check(summaries.Length == 1 && summaries[0].FailedCalls == 1 && !summaries[0].ExceedingMax,
            "Retain failed calls without describing a below-threshold maximum as slow.");
        Check(summaries[0].SumMs == timing.SumMs, "Preserve inclusive elapsed duration.");
    }

    public static void ExclusionsAndBounds()
    {
        var timings = Enum.GetValues(typeof(Metric)).Cast<Metric>().Select(metric => Sample(metric, 1000)).ToArray();
        var summaries = SlowOperationSummary.Create(timings.Concat(timings).ToArray());
        Check(summaries.Length == timings.Length - 3, "Exclude synthetic loop subdivisions and recorder self-timing; deduplicate metrics.");
        Check(summaries.Select(summary => summary.Operation).Distinct().Count() == summaries.Length,
            "Emit no more than one summary per metric per interval.");
        Check(summaries.Any(summary => summary.Operation == "CollectorPoll") &&
              summaries.Any(summary => summary.Operation == "CollectorSnapshot"), "Keep diagnostic collection costs visible.");
        Check(!summaries.Any(summary => summary.Operation == "LoopWithGcCollection" ||
              summary.Operation == "LoopAcrossPhaseBoundary" || summary.Operation == "TimingRecorder"),
            "Do not duplicate synthetic loop or self-recorder costs.");
        var unknown = Sample(Metric.ObjectCreate, 1000);
        unknown.Name = "unbounded-custom-operation";
        Check(SlowOperationSummary.Create(new[] { unknown }).Length == 0, "Only the fixed Metric vocabulary may emit summaries.");
        unknown.Name = "1";
        Check(SlowOperationSummary.Create(new[] { unknown }).Length == 0, "Numeric enum aliases are not metric names.");
    }

    public static void WorkerAndLoopInterpretation()
    {
        Check(SlowOperationSummary.Create(new[] { Sample(Metric.TerrainBuildWorker, 249) }).Length == 0,
            "Terrain service uses the worker threshold, not the main-thread threshold.");
        var terrain = SlowOperationSummary.Create(new[] { Sample(Metric.TerrainBuildWorker, 250), Sample(Metric.TerrainSyncWait, 20) });
        Check(terrain[0].Interpretation.Contains("worker service") && terrain[1].Interpretation.Contains("polling"),
            "Distinguish worker service from synchronous polling wait.");
        Check(SlowOperationSummary.Create(new[] { Sample(Metric.SaveWorker, 249.99), Sample(Metric.LoopInterval, 99.99) }).Length == 0,
            "Worker and loop durations use their own thresholds.");
        var summaries = SlowOperationSummary.Create(new[] { Sample(Metric.SaveWorker, 250), Sample(Metric.LoopInterval, 100), Sample(Metric.SavePrepare, 20) });
        Check(summaries.Length == 3 && summaries[0].ThresholdMs == 250 && summaries[1].ThresholdMs == 100 && summaries[2].ThresholdMs == 20,
            "Apply each threshold at its boundary.");
        Check(summaries[0].Interpretation.Contains("asynchronous") && summaries[0].Interpretation.Contains("main-thread"),
            "Do not describe asynchronous worker elapsed time as a main-thread stall.");
        Check(summaries[1].Interpretation.Contains("scheduling gap"), "A loop interval is not method execution time.");
        Check(summaries[2].Interpretation.IndexOf("inclusive", StringComparison.OrdinalIgnoreCase) >= 0, "Nested timings represent inclusive elapsed time.");
    }

    public static void IndependentSnapshotsAndValidation()
    {
        var input = Sample(Metric.SceneUpdate, 60);
        var first = SlowOperationSummary.Create(new[] { input });
        var second = SlowOperationSummary.Create(new[] { input });
        input.MaxMs = 1;
        input.Name = "changed";
        first[0].MaxMs = 999;
        Check(second[0].MaxMs == 60 && second[0].Operation == "SceneUpdate", "Snapshots must not alias input or other output objects.");
        Check(second[0].StallsOver50Ms == 1, "Preserve the separately measured >50 ms count.");
        Check(SlowOperationSummary.Create(Array.Empty<TimingSummary>()).Length == 0, "Empty intervals have no alerts.");
        foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
        {
            ExpectInvalid(() => SlowOperationSummary.Create(new[] { input }, invalid, 100, 250));
            ExpectInvalid(() => SlowOperationSummary.Create(new[] { input }, 20, invalid, 250));
            ExpectInvalid(() => SlowOperationSummary.Create(new[] { input }, 20, 100, invalid));
        }
    }

    private static TimingSummary Sample(Metric metric, double maximum) => new TimingSummary
    {
        Name = metric.ToString(), Count = 4, MaxMs = maximum, SumMs = maximum + 3,
        StallsOver50Ms = maximum > 50 ? 1 : 0
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Reject nonpositive or nonfinite thresholds.");
    }
}
