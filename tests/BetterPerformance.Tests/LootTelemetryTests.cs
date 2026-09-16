using System;
using BetterPerformance.Core;

internal static class LootTelemetryTests
{
    public static void Run()
    {
        CompletedWaitStartsAtFirstObservation();
        CapacityAndExpirationRemainBounded();
        MissingAndResetTracksAreCensored();
        IntervalDrainPreservesPendingTracks();
        ClockDiscontinuityDoesNotBecomeLatency();
        MembershipDoesNotAlterAccounting();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void MembershipDoesNotAlterAccounting()
    {
        var tracker = new LootQueueTracker<int>(2, 100, 500);
        Check(!tracker.Contains(1), "Empty tracker must allow a clock-free creation fast path.");
        tracker.Observe(1, 0);
        for (int i = 0; i < 1000; i++)
            Check(tracker.Contains(1) && !tracker.Contains(2), "Membership must distinguish tracked IDs without mutation.");
        var summary = tracker.Drain(20);
        Check(summary.Observed == 1 && summary.Pending == 1 && summary.Completed == 0 && summary.Censored == 0,
            "Fast-path membership reads must not change sample counts or age.");
        Check(tracker.Complete(1, 30) && !tracker.Contains(1), "Completed IDs must leave the membership index.");
        tracker.Observe(2, 40);
        tracker.Expire(140, 2);
        Check(!tracker.Contains(2), "Expired IDs must leave the membership index.");
        tracker.Observe(3, 150);
        tracker.Clear(false);
        Check(!tracker.Contains(3), "Capture reset must release the membership index.");
    }

    private static void CompletedWaitStartsAtFirstObservation()
    {
        var tracker = new LootQueueTracker<int>(2, 1000, 5000);
        Check(tracker.Observe(1, 100), "First observation should fit.");
        tracker.Observe(1, 200);
        Check(!tracker.Complete(2, 210), "Unobserved creations must not invent durations.");
        Check(tracker.Complete(1, 250), "Observed successful creation should complete.");
        Check(!tracker.Complete(1, 260), "A repeated postfix must not double-count.");
        var summary = tracker.Drain(300);
        Check(summary.Observed == 1 && summary.Completed == 1 && summary.CompletedSumMs == 150 &&
            summary.CompletedMaxMs == 150 && summary.Censored == 0 && summary.Pending == 0,
            "Repeated queue scans must preserve first observation and exactly one completion.");
    }

    private static void CapacityAndExpirationRemainBounded()
    {
        var tracker = new LootQueueTracker<int>(2, 100, 500);
        tracker.Observe(1, 0);
        tracker.Observe(2, 0);
        Check(!tracker.Observe(3, 0) && tracker.Count == 2, "Never grow beyond configured capacity.");
        tracker.Expire(100, 1);
        Check(tracker.Count == 1, "Expiration work should respect the slot budget.");
        Check(tracker.Observe(3, 100), "Expired slots must be reusable.");
        var summary = tracker.Drain(100);
        Check(summary.CapacitySkipped == 1 && summary.Censored == 2 && summary.Pending == 1,
            "Capacity skips and expiration censoring need separate accounting.");
        for (int i = 0; i < 10000; i++)
        {
            tracker.Observe(i, i + 1000);
            tracker.Complete(i, i + 1001);
        }
        Check(tracker.Count <= 2, "Long sessions must remain bounded after slot reuse.");
    }

    private static void MissingAndResetTracksAreCensored()
    {
        var tracker = new LootQueueTracker<int>(2, 100, 500);
        tracker.Observe(1, 0);
        Check(!tracker.Complete(1, 100), "Expired samples must not be reported as successful waits.");
        tracker.Observe(2, 101);
        tracker.Clear(true);
        var summary = tracker.Drain(200);
        Check(summary.Censored == 2 && summary.Completed == 0, "Missing and scene-change samples are censored, not completed.");
        tracker.Observe(3, 201);
        tracker.Clear(false);
        summary = tracker.Drain(202);
        Check(summary.Observed == 0 && summary.Censored == 0 && summary.Pending == 0,
            "Capture reset must discard previous capture state.");
        tracker.Observe(4, 300);
        for (int i = 1; i < 10; i++) tracker.Observe(4, 300 + i * 50);
        tracker.Expire(800, 2);
        Check(tracker.Drain(800).Censored == 1, "A constantly observed queue entry still needs a maximum lifetime.");
    }

    private static void IntervalDrainPreservesPendingTracks()
    {
        var tracker = new LootQueueTracker<int>(2, 1000, 5000);
        tracker.Observe(1, 10);
        var first = tracker.Drain(100);
        Check(first.Observed == 1 && first.Pending == 1 && first.PendingMaxAgeMs == 90, "Expose pending lower-bound age.");
        Check(tracker.Complete(1, 210), "Drain must preserve pending first-observation time.");
        var second = tracker.Drain(220);
        Check(second.Observed == 0 && second.Completed == 1 && second.CompletedSumMs == 200,
            "Completions belong to their completion interval, including wait across earlier intervals.");
        var empty = tracker.Drain(230);
        Check(empty.Completed == 0 && empty.CompletedSumMs == 0 && empty.CompletedMaxMs == 0,
            "Drain must not duplicate interval statistics.");
    }

    private static void ClockDiscontinuityDoesNotBecomeLatency()
    {
        var tracker = new LootQueueTracker<int>(2, 100, 500);
        tracker.Observe(1, 100);
        Check(!tracker.Complete(1, 90) && tracker.Drain(90).Censored == 1,
            "A backward clock invalidates a duration rather than becoming a zero-latency success.");
        bool rejected = false;
        try { tracker.Observe(2, double.NaN); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Reject invalid clocks before storing unbounded/invalid state.");
    }
}
