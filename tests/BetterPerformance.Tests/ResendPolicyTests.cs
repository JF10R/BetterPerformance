using System;
using System.Collections.Generic;
using System.Linq;
using BetterPerformance.Core;

internal static class ResendPolicyTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Resend policy: " + message);
    }

    internal static void Run()
    {
        Decisions();
        Bounds();
        Histograms();
        KeyedHistograms();
    }

    private static void Decisions()
    {
        var policy = new ResendPolicy(0.2);

        // Everything outside the throttled set leaves before the interval is consulted,
        // including entries whose age is well inside it.
        Check(policy.Evaluate(cosmetic: false, owned: true, prioritized: false, forced: false, hasPreviousSend: true, 0.0) == ResendDecision.Allow,
            "a non-cosmetic prefab is never deferred");
        Check(policy.Evaluate(true, true, false, forced: true, hasPreviousSend: true, 0.0) == ResendDecision.Allow,
            "a forced send is never deferred");
        Check(policy.Evaluate(true, true, prioritized: true, forced: false, hasPreviousSend: true, 0.0) == ResendDecision.Allow,
            "a prioritized object is never deferred");
        Check(policy.Evaluate(true, owned: false, prioritized: false, forced: false, hasPreviousSend: true, 0.0) == ResendDecision.Allow,
            "an object this process does not own is never deferred");
        Check(policy.Evaluate(true, true, false, false, hasPreviousSend: false, 0.0) == ResendDecision.Allow,
            "the first send to a peer always goes");

        Check(policy.Evaluate(true, true, false, false, true, 0.199) == ResendDecision.Defer, "inside the interval defers");
        Check(policy.Evaluate(true, true, false, false, true, 0.2) == ResendDecision.Allow, "the interval boundary is inclusive");
        Check(policy.Evaluate(true, true, false, false, true, 5.0) == ResendDecision.Allow, "beyond the interval allows");

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -0.001 })
            Check(policy.Evaluate(true, true, false, false, true, invalid) == ResendDecision.Allow,
                "an unusable age allows rather than defers");

        ResendCounters counters = policy.Counters;
        Check(counters.Considered == 11 && counters.Deferred == 1 && counters.Allowed == 10, "every decision is counted exactly once");
        Check(counters.OtherPrefabKept == 1 && counters.ForcedKept == 1 && counters.PrioritizedKept == 1 &&
              counters.ForeignKept == 1 && counters.FirstSends == 1 && counters.InvalidAges == 3, "reasons are attributed");
        policy.Reset();
        Check(policy.Counters.Considered == 0 && policy.Counters.Deferred == 0, "reset clears the interval");
        Check(Math.Abs(policy.IntervalSeconds - 0.2) < 1e-9, "reset preserves the configured interval");
    }

    private static void Bounds()
    {
        foreach (double outside in new[] { 0.0, 0.049, 1.01, double.NaN })
        {
            bool rejected = false;
            try { _ = new ResendPolicy(outside); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "an interval outside 0.05-1.0 seconds is rejected: " + outside);
        }
        _ = new ResendPolicy(ResendPolicy.MinimumIntervalSeconds);
        _ = new ResendPolicy(ResendPolicy.MaximumIntervalSeconds);
    }

    private static void Histograms()
    {
        var histogram = new BucketHistogram(new double[] { 0.02, 0.05, 0.1 });
        Check(histogram.BucketCount == 4, "bounds plus one open-ended bucket");
        histogram.Add(0);
        histogram.Add(0.019);
        histogram.Add(0.02);
        histogram.Add(0.09);
        histogram.Add(1000);
        Check(histogram[0] == 2 && histogram[1] == 1 && histogram[2] == 1 && histogram[3] == 1, "values land in the bucket below their upper bound");
        Check(histogram.Total == 5 && histogram.Rejected == 0, "every accepted value is totalled");
        histogram.Add(double.NaN);
        histogram.Add(-1);
        Check(histogram.Total == 5 && histogram.Rejected == 2, "unusable values are rejected, not bucketed");
        var copy = new long[4];
        histogram.CopyTo(copy);
        Check(copy.Sum() == 5, "a copy carries every count");
        histogram.Reset();
        Check(histogram.Total == 0 && histogram.Rejected == 0 && histogram[0] == 0, "reset clears counts and rejections");

        bool unordered = false;
        try { _ = new BucketHistogram(new double[] { 1, 1 }); } catch (ArgumentException) { unordered = true; }
        Check(unordered, "non-ascending bounds are rejected");
    }

    private static void KeyedHistograms()
    {
        var keyed = new KeyedBucketHistograms(2, new double[] { 0.05, 0.2 });
        Check(keyed.BucketCount == 3, "keyed histograms carry the same bucket layout");
        Check(keyed.Add(1, 0.01) && keyed.Add(1, 0.3) && keyed.Add(2, 0.1), "keys within capacity are tracked");
        Check(!keyed.Add(3, 0.1) && keyed.DroppedSamples == 1 && keyed.TrackedKeys == 2, "a key beyond capacity is counted and dropped");
        Check(keyed.Add(2, 0.1), "an already tracked key keeps recording after capacity is reached");

        List<KeyValuePair<int, BucketHistogram>> top = keyed.TopKeys(1);
        Check(top.Count == 1 && top[0].Key == 1 && top[0].Value.Total == 2, "the busiest key is exported first");
        Check(keyed.TopKeys(-1).Count == 2, "a negative count exports every key");

        var ties = new KeyedBucketHistograms(4, new double[] { 1 });
        ties.Add(9, 0.5);
        ties.Add(4, 0.5);
        Check(ties.TopKeys(2).Select(row => row.Key).SequenceEqual(new[] { 4, 9 }), "equal totals export in stable key order");

        Check(BucketHistogram.Describe(new double[] { 0.02, 0.5, 2 }) == "0.02,0.5,2,inf", "bucket edges are exported with the counts");

        keyed.Reset();
        Check(keyed.TrackedKeys == 0 && keyed.DroppedSamples == 0, "reset clears keys and drops");
    }
}
