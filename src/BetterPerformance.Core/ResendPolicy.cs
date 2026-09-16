using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    public enum ResendDecision { Allow, Defer }

    // Per-interval accounting of one send-selection pass. Counters are additive and
    // never reset by Evaluate, so an exporter can drain them at its own cadence.
    public struct ResendCounters
    {
        public long Considered, Allowed, Deferred, FirstSends, ForcedKept, PrioritizedKept, ForeignKept, OtherPrefabKept, InvalidAges;
    }

    // Decides whether an already-selected replication entry may be skipped for this
    // cycle. Deferral is only ever a delay: the caller keeps the entry queued and the
    // next send carries the full current state, because serialization is never a delta.
    public sealed class ResendPolicy
    {
        public const double MinimumIntervalSeconds = 0.05, MaximumIntervalSeconds = 1.0;
        private ResendCounters counters;

        public ResendPolicy(double intervalSeconds)
        {
            if (double.IsNaN(intervalSeconds) || intervalSeconds < MinimumIntervalSeconds || intervalSeconds > MaximumIntervalSeconds)
                throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            IntervalSeconds = intervalSeconds;
        }

        public double IntervalSeconds { get; }
        public ResendCounters Counters => counters;

        // Order is a safety ladder, not an optimization: anything forced, prioritized
        // or owned by another process leaves before the interval is ever consulted.
        public ResendDecision Evaluate(bool cosmetic, bool owned, bool prioritized, bool forced, bool hasPreviousSend, double secondsSincePreviousSend)
        {
            counters.Considered++;
            if (!cosmetic) return Allow(ref counters.OtherPrefabKept);
            if (forced) return Allow(ref counters.ForcedKept);
            if (prioritized) return Allow(ref counters.PrioritizedKept);
            if (!owned) return Allow(ref counters.ForeignKept);
            if (!hasPreviousSend) return Allow(ref counters.FirstSends);
            if (double.IsNaN(secondsSincePreviousSend) || double.IsInfinity(secondsSincePreviousSend) || secondsSincePreviousSend < 0)
                return Allow(ref counters.InvalidAges);
            if (secondsSincePreviousSend >= IntervalSeconds) { counters.Allowed++; return ResendDecision.Allow; }
            counters.Deferred++;
            return ResendDecision.Defer;
        }

        private ResendDecision Allow(ref long reason) { reason++; counters.Allowed++; return ResendDecision.Allow; }

        public void Reset() => counters = default;
    }

    // Fixed-bound histogram. The last bound is the open-ended bucket; values are never
    // stored, so memory is one long per bucket regardless of sample count.
    public sealed class BucketHistogram
    {
        private readonly double[] bounds;
        private readonly long[] counts;

        public BucketHistogram(double[] upperBounds)
        {
            if (upperBounds == null || upperBounds.Length == 0) throw new ArgumentException("At least one bound is required.", nameof(upperBounds));
            for (int i = 1; i < upperBounds.Length; i++)
                if (!(upperBounds[i] > upperBounds[i - 1])) throw new ArgumentException("Bounds must ascend.", nameof(upperBounds));
            bounds = (double[])upperBounds.Clone();
            counts = new long[bounds.Length + 1];
        }

        public int BucketCount => counts.Length;
        public long Total { get; private set; }
        public long Rejected { get; private set; }

        public void Add(double value)
        {
            if (double.IsNaN(value) || value < 0) { Rejected++; return; }
            int bucket = bounds.Length;
            for (int i = 0; i < bounds.Length; i++)
                if (value < bounds[i]) { bucket = i; break; }
            counts[bucket]++;
            Total++;
        }

        public long this[int bucket] => counts[bucket];

        public void CopyTo(long[] destination)
        {
            if (destination == null || destination.Length < counts.Length) throw new ArgumentException("Destination too small.", nameof(destination));
            Array.Copy(counts, destination, counts.Length);
        }

        public void Reset()
        {
            Array.Clear(counts, 0, counts.Length);
            Total = 0;
            Rejected = 0;
        }

        // Stable text form of the bucket edges, exported beside the counts so a reader
        // never has to guess what bucket index k means.
        public static string Describe(double[] upperBounds)
        {
            var text = new System.Text.StringBuilder();
            foreach (double bound in upperBounds)
            {
                if (text.Length > 0) text.Append(',');
                text.Append(bound.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            }
            return text.Append(",inf").ToString();
        }
    }

    // Bounded set of histograms keyed by an integer (a prefab hash). Once the key
    // capacity is reached, further keys are counted and dropped rather than tracked,
    // so an unexpected prefab mix cannot grow this without limit.
    public sealed class KeyedBucketHistograms
    {
        private readonly Dictionary<int, BucketHistogram> histograms;
        private readonly double[] bounds;
        private readonly int keyCapacity;

        public KeyedBucketHistograms(int keyCapacity, double[] upperBounds)
        {
            if (keyCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(keyCapacity));
            _ = new BucketHistogram(upperBounds);
            this.keyCapacity = keyCapacity;
            bounds = (double[])upperBounds.Clone();
            histograms = new Dictionary<int, BucketHistogram>(keyCapacity);
        }

        public int TrackedKeys => histograms.Count;
        public long DroppedSamples { get; private set; }
        public int BucketCount => bounds.Length + 1;

        public bool Add(int key, double value)
        {
            if (!histograms.TryGetValue(key, out BucketHistogram histogram))
            {
                if (histograms.Count >= keyCapacity) { DroppedSamples++; return false; }
                histogram = new BucketHistogram(bounds);
                histograms.Add(key, histogram);
            }
            histogram.Add(value);
            return true;
        }

        // Drain-time only. Ordered by sample count, then by key for a stable export
        // when two prefabs tie.
        public List<KeyValuePair<int, BucketHistogram>> TopKeys(int count)
        {
            var rows = new List<KeyValuePair<int, BucketHistogram>>(histograms);
            rows.Sort((left, right) =>
            {
                int byTotal = right.Value.Total.CompareTo(left.Value.Total);
                return byTotal != 0 ? byTotal : left.Key.CompareTo(right.Key);
            });
            if (count >= 0 && rows.Count > count) rows.RemoveRange(count, rows.Count - count);
            return rows;
        }

        public void Reset()
        {
            histograms.Clear();
            DroppedSamples = 0;
        }
    }
}
