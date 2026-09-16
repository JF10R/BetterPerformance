using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BetterPerformance.Core
{
    // Derived from interval aggregates, never from per-call logs. TotalCalls includes
    // every call in the interval: the histogram cannot give an exact count at an
    // arbitrary threshold. StallsOver50Ms retains its separate, fixed >50 ms meaning.
    [DataContract]
    public sealed class SlowOperationSummary
    {
        [DataMember(Name = "operation")] public string Operation { get; set; } = "";
        [DataMember(Name = "totalCalls")] public long TotalCalls { get; set; }
        [DataMember(Name = "maxMs")] public double MaxMs { get; set; }
        [DataMember(Name = "sumMs")] public double SumMs { get; set; }
        [DataMember(Name = "thresholdMs")] public double ThresholdMs { get; set; }
        // True means the interval maximum meets or exceeds the configured threshold.
        [DataMember(Name = "exceedingMax")] public bool ExceedingMax { get; set; }
        [DataMember(Name = "stallsOver50Ms")] public long StallsOver50Ms { get; set; }
        [DataMember(Name = "failedCalls")] public long FailedCalls { get; set; }
        [DataMember(Name = "interpretation")] public string Interpretation { get; set; } = "";

        private static readonly int MetricCount = Enum.GetValues(typeof(Metric)).Length;

        public static SlowOperationSummary[] Create(TimingSummary[] timings,
            double methodMs = 20, double loopMs = 100, double workerMs = 250)
        {
            if (timings == null) throw new ArgumentNullException(nameof(timings));
            ValidateThreshold(methodMs, nameof(methodMs));
            ValidateThreshold(loopMs, nameof(loopMs));
            ValidateThreshold(workerMs, nameof(workerMs));
            var summaries = new List<SlowOperationSummary>(Math.Min(timings.Length, MetricCount));
            var seen = new HashSet<Metric>();
            foreach (var timing in timings)
            {
                // MetricBook supplies exactly one aggregate per metric. Keep the first
                // if a caller supplies duplicates, so output still has a fixed bound.
                if (timing == null || !Enum.TryParse(timing.Name, out Metric metric) ||
                    !Enum.IsDefined(typeof(Metric), metric) || metric.ToString() != timing.Name || !seen.Add(metric)) continue;
                if (metric == Metric.LoopWithGcCollection || metric == Metric.LoopAcrossPhaseBoundary ||
                    metric == Metric.TimingRecorder) continue;
                double threshold = metric == Metric.LoopInterval ? loopMs :
                    metric == Metric.SaveWorker || metric == Metric.TerrainBuildWorker ? workerMs : methodMs;
                bool exceeding = timing.Count > 0 && timing.MaxMs >= threshold;
                if (!exceeding && timing.FailedCalls <= 0) continue;
                summaries.Add(new SlowOperationSummary
                {
                    Operation = timing.Name, TotalCalls = timing.Count, MaxMs = timing.MaxMs,
                    SumMs = timing.SumMs, ThresholdMs = threshold, ExceedingMax = exceeding,
                    StallsOver50Ms = timing.StallsOver50Ms, FailedCalls = timing.FailedCalls,
                    Interpretation = metric == Metric.LoopInterval
                        ? "Loop scheduling gap; includes pacing and waiting, not method execution time."
                        : metric == Metric.SaveWorker
                            ? "Inclusive elapsed time in an asynchronous save worker; not a main-thread stall."
                            : metric == Metric.TerrainBuildWorker
                                ? "Inclusive terrain worker service time; excludes queue wait and is not a main-thread stall."
                            : metric == Metric.TerrainSyncWait
                                ? "Inclusive synchronous terrain request wait, including polling; not terrain-generation CPU."
                            : "Inclusive elapsed method time; nested timings can overlap and must not be added together."
                });
            }
            return summaries.ToArray();
        }

        private static void ValidateThreshold(double value, string name)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Threshold must be finite and positive.");
        }
    }
}
