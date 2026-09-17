using System;
using BetterPerformance.Core;

internal static class LootVisibilityTests
{
    public static void Run()
    {
        ConstructorRejectsUnusableBounds();
        RadiusAndWindowBoundAttribution();
        NearestDestructionWins();
        BothBoundsOverflowWithoutGrowing();
        ArrivalMissingIsTheLocallyOwnedControl();
        BucketBoundariesAreInclusiveUpperBounds();
        BackwardClockClampsInsteadOfInventingDuration();
        DrainResetsTheIntervalAndClearCensors();
        UnclaimedArrivalsExpireInsteadOfFillingTheMap();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static LootVisibilityTracker<int> Tracker(double radius = 10, double window = 5000)
        => new LootVisibilityTracker<int>(32, 256, radius, window);

    private static void Rejects(Action action, string message)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void ConstructorRejectsUnusableBounds()
    {
        Rejects(() => new LootVisibilityTracker<int>(0, 256, 10, 5000), "Zero destruction capacity is unbounded intent.");
        Rejects(() => new LootVisibilityTracker<int>(32, 0, 10, 5000), "Zero arrival capacity is unbounded intent.");
        Rejects(() => new LootVisibilityTracker<int>(32, 256, 0, 5000), "A zero radius can never attribute anything.");
        Rejects(() => new LootVisibilityTracker<int>(32, 256, double.PositiveInfinity, 5000), "An infinite radius attributes everything.");
        Rejects(() => new LootVisibilityTracker<int>(32, 256, 10, 0), "A zero window can never attribute anything.");
        Rejects(() => new LootVisibilityTracker<int>(32, 256, 10, double.NaN), "An invalid window must not be stored.");
        var tracker = Tracker();
        Rejects(() => tracker.Destroyed(0, 0, 0, double.NaN), "Reject an invalid clock before storing state.");
        Rejects(() => tracker.Destroyed(double.PositiveInfinity, 0, 0, 0), "Reject an invalid coordinate before storing state.");
        Rejects(() => tracker.Created(1, double.NaN, 0, 0, 0, false), "Reject an invalid creation coordinate.");
        Rejects(() => tracker.Arrived(1, 0, 0, 0, double.PositiveInfinity), "Reject an invalid arrival clock.");
    }

    private static void RadiusAndWindowBoundAttribution()
    {
        var tracker = Tracker(radius: 10, window: 1000);
        tracker.Destroyed(0, 0, 0, 100);
        Check(!tracker.Arrived(1, 0, 0, 11, 110), "A drop outside the radius must not be stored as an arrival.");
        Check(!tracker.Created(1, 0, 0, 11, 120, false), "A drop outside the radius is unattributed, not a long delay.");
        Check(tracker.Arrived(2, 0, 0, 9, 110), "A drop inside the radius is an eligible arrival.");
        Check(tracker.Created(2, 0, 0, 9, 120, false), "A drop inside the radius completes an observation.");
        Check(!tracker.Created(3, 0, 0, 0, 1100, false), "A drop after the window is unattributed.");
        var summary = tracker.Drain(1200);
        Check(summary.Unattributed == 2 && summary.PerceivedCount == 1, "Radius and window rejections are counted, never timed.");
        Check(summary.NetworkCount == 1 && summary.NetworkSumMs == 10 &&
              summary.CreationCount == 1 && summary.CreationSumMs == 10 &&
              summary.PerceivedSumMs == 20 && summary.PerceivedMaxMs == 20,
            "The two legs must sum to the perceived duration.");
        Check(summary.PendingDestructions == 0, "Drain must expire destructions past the window.");
    }

    private static void NearestDestructionWins()
    {
        var tracker = Tracker(radius: 10);
        tracker.Destroyed(0, 0, 0, 100);
        tracker.Destroyed(5, 0, 0, 200);
        Check(tracker.Created(1, 4, 0, 0, 300, false), "A drop between two destructions must attribute.");
        var summary = tracker.Drain(300);
        Check(summary.PerceivedSumMs == 100, "The nearest live destruction wins, not the oldest.");
        Check(summary.PendingDestructions == 2, "Attribution must not consume a destruction; one chunk drops several items.");
    }

    private static void BothBoundsOverflowWithoutGrowing()
    {
        var tracker = new LootVisibilityTracker<int>(2, 2, 10, 5000);
        for (int i = 0; i < 5; i++) tracker.Destroyed(0, 0, 0, 100);
        Check(tracker.PendingDestructions == 2, "The destruction ring must never grow beyond its capacity.");
        for (int i = 0; i < 5; i++) tracker.Arrived(i, 0, 0, 0, 110);
        Check(tracker.PendingArrivals == 2, "The arrival map must never grow beyond its capacity.");
        var summary = tracker.Drain(120);
        Check(summary.Overflowed == 3 && summary.ArrivalCapacitySkipped == 3,
            "Overwritten destructions and rejected arrivals need separate accounting.");
    }

    private static void ArrivalMissingIsTheLocallyOwnedControl()
    {
        var tracker = Tracker();
        tracker.Destroyed(0, 0, 0, 100);
        Check(tracker.Created(1, 0, 0, 0, 150, true), "A locally created drop still has a perceived duration.");
        var summary = tracker.Drain(200);
        Check(summary.ArrivalMissing == 1 && summary.LocallyOwned == 1, "No arrival means the local process made the drop.");
        Check(summary.NetworkCount == 0 && summary.CreationCount == 0 && summary.PerceivedCount == 1,
            "An absent arrival must not invent a network or creation leg.");
    }

    private static void BucketBoundariesAreInclusiveUpperBounds()
    {
        var tracker = Tracker(window: 100000);
        double[] durations = { 16, 16.001, 32, 64, 128, 256, 512, 1024, 1024.001, 50000 };
        for (int i = 0; i < durations.Length; i++)
        {
            tracker.Destroyed(100 * i, 0, 0, 0);
            tracker.Created(i, 100 * i, 0, 0, durations[i], false);
        }
        var summary = tracker.Drain(2000);
        Check(summary.PerceivedBuckets.Length == LootVisibilityTracker<int>.BucketCount, "One histogram of eight buckets.");
        Check(summary.PerceivedBuckets[0] == 1, "An exact upper bound belongs to its own bucket.");
        Check(summary.PerceivedBuckets[1] == 2, "Just over a bound moves to the next bucket.");
        for (int i = 2; i < 7; i++) Check(summary.PerceivedBuckets[i] == 1, "Each intermediate bound fills its own bucket.");
        Check(summary.PerceivedBuckets[7] == 2, "The overflow bucket holds everything above the last bound.");
    }

    private static void BackwardClockClampsInsteadOfInventingDuration()
    {
        var tracker = Tracker();
        tracker.Destroyed(0, 0, 0, 100);
        tracker.Arrived(1, 0, 0, 0, 150);
        // A creation before its own arrival: the arrival leg is clamped, not negative.
        Check(tracker.Created(1, 0, 0, 0, 120, false), "A backward clock must still complete the observation.");
        var summary = tracker.Drain(200);
        Check(summary.NonMonotonic == 1 && summary.CreationSumMs == 0, "A negative duration becomes zero and is counted.");
        Check(summary.NetworkSumMs == 50 && summary.PerceivedSumMs == 20, "Clamping must not disturb the other legs.");
    }

    private static void DrainResetsTheIntervalAndClearCensors()
    {
        var tracker = Tracker();
        tracker.Destroyed(0, 0, 0, 100);
        tracker.Created(1, 0, 0, 0, 150, false);
        var first = tracker.Drain(200);
        Check(first.PerceivedCount == 1, "The first interval owns its observation.");
        var second = tracker.Drain(210);
        Check(second.PerceivedCount == 0 && second.PerceivedSumMs == 0 && second.ArrivalMissing == 0,
            "Drain must not duplicate interval statistics.");
        foreach (long bucket in second.PerceivedBuckets) Check(bucket == 0, "Drain must reset the histogram.");
        Check(second.PendingDestructions == 1, "A destruction still inside its window stays pending across drains.");
        tracker.Arrived(2, 0, 0, 0, 220);
        tracker.Clear(true);
        var censored = tracker.Drain(230);
        Check(censored.Censored == 2 && tracker.PendingDestructions == 0 && tracker.PendingArrivals == 0,
            "A scene change censors pending destructions and arrivals rather than completing them.");
        tracker.Destroyed(0, 0, 0, 300);
        tracker.Clear(false);
        var reset = tracker.Drain(310);
        Check(reset.Censored == 0 && reset.PerceivedCount == 0, "A new capture resets counters instead of censoring.");
    }

    // An arrival is recorded before the prefab is known, so most arrivals near a mined
    // rock are never claimed by Created: only a loot prefab reaches it. They must expire,
    // or a long session fills the map and silently stops recording the network leg.
    private static void UnclaimedArrivalsExpireInsteadOfFillingTheMap()
    {
        var tracker = new LootVisibilityTracker<int>(32, 4, 10, 5000);
        tracker.Destroyed(0, 0, 0, 1000);
        for (int key = 0; key < 4; key++) Check(tracker.Arrived(key, 0, 0, 0, 1000 + key), "A nearby arrival is recorded.");
        Check(!tracker.Arrived(99, 0, 0, 0, 1010), "A full arrival map skips rather than growing.");
        var full = tracker.Drain(1020);
        Check(full.ArrivalCapacitySkipped == 1 && full.ArrivalsExpired == 0 && tracker.PendingArrivals == 4,
            "Inside the window every unclaimed arrival is still held.");

        // The window is per arrival, so the last one recorded sets the deadline.
        var expiring = tracker.Drain(1003 + 5000);
        Check(expiring.ArrivalsExpired == 4 && tracker.PendingArrivals == 0,
            "Past the window an unclaimed arrival can no longer match and must be released.");
        tracker.Destroyed(0, 0, 0, 6000);
        Check(tracker.Arrived(5, 0, 0, 0, 6001), "Released capacity is reusable.");

        // An expired arrival must not resurrect as a network observation.
        tracker.Drain(6001 + 5000);
        tracker.Destroyed(0, 0, 0, 12000);
        Check(tracker.Created(5, 0, 0, 0, 12010, false), "The drop is still attributed to its destruction.");
        var after = tracker.Drain(12020);
        Check(after.PerceivedCount == 1 && after.NetworkCount == 0 && after.ArrivalMissing == 1,
            "A dropped arrival is reported as missing, never as a stale network duration.");
    }
}
