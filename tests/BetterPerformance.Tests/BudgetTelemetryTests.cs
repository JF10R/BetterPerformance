using System;
using BetterPerformance.Core;

internal static class BudgetTelemetryTests
{
    public static void YieldWaitAndBounds()
    {
        var telemetry = new BudgetTelemetry<int>(2, 30, 120);
        telemetry.RecordYield(1, false, 0);
        telemetry.RecordYield(1, false, 10);
        telemetry.RecordYield(2, true, 12);
        telemetry.RecordYield(3, false, 13);
        telemetry.RecordCreation(1, 0.4, 4, true, false, 20);
        var summary = telemetry.Drain(20);
        Check(summary.NearYields == 3 && summary.DistantYields == 1, "Count loop exits, not unique candidates.");
        Check(summary.Waits.Observed == 2 && summary.Waits.CapacitySkipped == 1 && summary.Waits.Pending == 1,
            "Bound tracked IDs and expose missed tracking.");
        Check(summary.Waits.Completed == 1 && summary.Waits.CompletedSumMs == 20,
            "Repeated yields preserve first observed yield time.");
        telemetry.CompletePending(2, 25);
        var next = telemetry.Drain(25);
        Check(next.Waits.Completed == 1 && next.Waits.CompletedSumMs == 13 && next.CreationCalls == 0,
            "Observe completion after a disabled budget without inventing budget-active call cost.");
        Check(next.NearYields == 0 && next.DistantYields == 0, "Drain interval counters independently of pending observations.");
    }

    public static void CreationCostsAndBatches()
    {
        var telemetry = new BudgetTelemetry<int>();
        telemetry.RecordCreation(1, 4, 4, true, false, 10);
        telemetry.RecordCreation(2, 6, 4, false, false, 20);
        telemetry.RecordCreation(3, 2, 4, false, true, 30);
        telemetry.RecordBatch(3, 1, true, 13, 4);
        telemetry.RecordBatch(0, 0, false, 4, 4);
        var summary = telemetry.Drain(31);
        Check(summary.CreationCalls == 3 && summary.CreationNullResults == 1 && summary.CreationFailures == 1,
            "Distinguish successful, null and throwing native calls.");
        Check(summary.CreationOvershoots == 1 && summary.CreationExcessSumMs == 2 && summary.CreationMaxMs == 6,
            "An overshoot strictly exceeds the full batch allowance; equal cost is not excess.");
        Check(summary.CreationSumMs == 12 && summary.BatchSumMs == 17 && summary.BatchMaxMs == 13,
            "Keep inclusive creation and batch elapsed summaries separate.");
        Check(summary.Batches == 2 && summary.YieldedBatches == 1 && summary.Attempts == 3 && summary.Successes == 1 && summary.BatchOvershoots == 1,
            "Batch outcomes are not inferred from yield or candidate counts.");
    }

    public static void CensoringAndReset()
    {
        var telemetry = new BudgetTelemetry<int>(2, 30, 120);
        telemetry.RecordYield(1, false, 0);
        telemetry.RecordCreation(1, 1, 4, false, false, 10);
        Check(telemetry.Drain(10).Waits.Pending == 1, "A failed prefab result does not complete a wait.");
        telemetry.CompletePending(1, 30);
        var expired = telemetry.Drain(30);
        Check(expired.Waits.Censored == 1 && expired.Waits.Completed == 0, "Expired observations are censored, not successful waits.");
        telemetry.RecordYield(2, false, 40);
        telemetry.Clear(true);
        Check(telemetry.Drain(41).Waits.Censored == 1, "Scene/capture-end cleanup censors pending tracks.");
        telemetry.RecordYield(3, false, 50);
        telemetry.Clear(false);
        var reset = telemetry.Drain(51);
        Check(reset.NearYields == 0 && reset.Waits.Observed == 0 && reset.Waits.Pending == 0,
            "A fresh capture must not inherit previous counters or identities.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
