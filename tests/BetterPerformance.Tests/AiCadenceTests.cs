using System;
using BetterPerformance.Core;

internal static class AiCadenceTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var cadence = new AiCadence();
        Check(cadence.Observe(1, 10, 3, 0, 0.05), "Accept first AI batch.");
        Check(cadence.Observe(1.1, 11, 4, 1, 0.05), "Accept next frame.");
        Check(cadence.Observe(1.101, 11, 4, 0, 0.05), "Accept repeated frame.");
        AiCadenceSnapshot first = cadence.Drain();
        Check(first.Batches == 3 && first.RepeatedFrameBatches == 1, "Count batches and repeats, not individual AI updates.");
        Check(first.GapCount == 2 && Math.Abs(first.GapSumMs - 101) < 0.0001, "Measure wall gaps independently from supplied dt.");
        Check(Math.Abs(first.DeltaSumMs - 150) < 0.0001, "Keep supplied simulation time separate.");
        Check(first.InputCountSum == 11 && first.InputCountMax == 4 && first.ScratchCountMax == 1,
            "Preserve constant-time list observations, including leftover scratch entries.");
        Check(cadence.Observe(1.151, 11, 2, 0, 0.05), "Observe after export.");
        AiCadenceSnapshot second = cadence.Drain();
        Check(second.Batches == 1 && second.RepeatedFrameBatches == 1 && second.GapCount == 1,
            "Export resets interval counters but preserves cadence boundary.");
        Check(Math.Abs(second.GapSumMs - 50) < 0.0001, "Cross-export wall gap is preserved.");
        Check(!cadence.Observe(double.NaN, 12, 1, 0, 0.05) && !cadence.Observe(1.0, 12, 1, 0, 0.05),
            "Reject invalid and regressing clocks.");
        Check(!cadence.Observe(1.2, 12, -1, 0, 0.05) && !cadence.Observe(1.2, 12, 1, 0, double.PositiveInfinity),
            "Reject invalid list counts and delta values.");
        AiCadenceSnapshot rejected = cadence.Drain();
        Check(rejected.Batches == 0 && rejected.Rejected == 4 && rejected.GapCount == 0,
            "Invalid observations must not fabricate activity.");
        cadence.Reset();
        Check(cadence.Observe(0, 0, 0, 0, 0.05), "Reset accepts a fresh capture epoch and empty population.");
        AiCadenceSnapshot fresh = cadence.Drain();
        Check(fresh.Batches == 1 && fresh.InputCountSum == 0 && fresh.GapCount == 0 && fresh.RepeatedFrameBatches == 0,
            "A new capture must not inherit time or frame state.");
        Check(first.Batches == 3, "Drained snapshots are independent values.");
    }
}
