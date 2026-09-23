using System;
using BetterPerformance.Core;

internal static class LootVisibilityTests
{
    public static void Run()
    {
        ConstructorRejectsUnusableBounds();
        RadiusAndWindowBoundAttribution();
        AmbiguousDestructionsAreExcluded();
        ArrivalKeepsItsOriginalDestruction();
        BothBoundsOverflowWithoutGrowing();
        MissingArrivalCannotInventLatency();
        BucketBoundariesAreInclusiveUpperBounds();
        BackwardClockExcludesInvalidDuration();
        DrainResetsTheIntervalAndClearCensors();
        UnclaimedArrivalsExpireInsteadOfFillingTheMap();
        OwnedSourcesAreNeverNetworkSources();
        DropTablesSplitForeignSingleAndAmbiguous();
        KindRadiusIsMeasuredFromTheSource();
        FifthCandidateMakesTheArrivalAmbiguous();
        WitnessesKeepTheSlowestWithinBounds();
        OwnerLegMatchesOnlyItsOwnSingleSource();
        OldDropReenteringViewIsStale();
        LootSendLegTests.Run();
    }

    private static void OldDropReenteringViewIsStale()
    {
        var tracker = Tables(window: 5000);
        tracker.Destroyed(0, 0, 0, 100, 100, 5, false, 4);
        tracker.Arrived(1, 1, 0, 0, 150);
        Check(!tracker.Created(1, 1, 0, 0, 160, false, 1, 60000, out _),
            "A drop spawned a minute ago is an old drop re-entering view, never this destruction's loot.");
        tracker.Arrived(2, 1, 0, 0, 150);
        Check(tracker.Created(2, 1, 0, 0, 160, false, 1, 5000 + LootVisibilityTracker<int>.StaleMarginMs, out _),
            "At the margin the synced-clock offset is tolerated.");
        tracker.Arrived(3, 1, 0, 0, 150);
        Check(tracker.Created(3, 1, 0, 0, 160, false, 1, double.NaN, out _), "An unknown spawn age does not exclude.");
        var summary = tracker.Drain(200);
        Check(summary.Stale == 1 && summary.PerceivedCount == 2, "Stale drops are counted and excluded.");
    }

    // Source prefab 100 drops item 1, 200 drops item 2, 300 drops items 1 and 2.
    private static bool Table(int source, int drop) =>
        (source == 100 && drop == 1) || (source == 200 && drop == 2) || (source == 300 && (drop == 1 || drop == 2));

    private static LootVisibilityTracker<int> Tables(double radius = 12, double window = 5000)
        => new LootVisibilityTracker<int>(32, 256, radius, window, Table);

    private static void OwnedSourcesAreNeverNetworkSources()
    {
        var tracker = Tables();
        tracker.Destroyed(0, 0, 0, 100, 100, 5, owned: true, 4);
        tracker.Destroyed(3, 0, 0, 100, 100, 5, owned: false, 4);
        Check(tracker.Arrived(1, 1, 0, 0, 150), "An arrival near both sources is kept.");
        Check(tracker.Created(1, 1, 0, 0, 170, false, 1, double.NaN, out _),
            "With our own rock excluded, the one remote source times the drop (v2 called it ambiguous).");
        tracker.Destroyed(50, 0, 0, 200, 100, 5, owned: true, 4);
        Check(tracker.Arrived(2, 50, 0, 1, 250), "An arrival near only our own rock is still classified at creation.");
        Check(!tracker.Created(2, 50, 0, 1, 260, false, 1, double.NaN, out _), "A network drop can never come from a source we owned.");
        var summary = tracker.Drain(300);
        Check(summary.PerceivedCount == 1 && summary.PerceivedSumMs == 70, "Only the remote source is timed, from its own t0.");
        Check(summary.OwnedSourceExcluded == 2 && summary.OwnedOnly == 1 && summary.Ambiguous == 0,
            "Owned exclusions are counted, and a drop near only our own source is owned-only, not ambiguous.");
        Check(summary.Witnesses.Length == 1 && summary.Witnesses[0].OwnedExcluded == 1, "The witness names the exclusion.");
    }

    private static void DropTablesSplitForeignSingleAndAmbiguous()
    {
        var tracker = Tables();
        tracker.Destroyed(0, 0, 0, 100, 100, 5, false, 4);
        tracker.Destroyed(2, 0, 0, 100, 200, 5, false, 4);
        tracker.Arrived(1, 1, 0, 0, 150);
        Check(tracker.Created(1, 1, 0, 0, 160, false, 2, double.NaN, out _), "Only the table that holds the item survives.");
        tracker.Arrived(2, 1, 0, 0, 150);
        Check(!tracker.Created(2, 1, 0, 0, 160, false, 7, double.NaN, out _), "An item no candidate can spawn is foreign.");
        tracker.Destroyed(1, 1, 0, 100, 300, 5, false, 4);
        tracker.Arrived(3, 1, 0, 0, 150);
        Check(!tracker.Created(3, 1, 0, 0, 160, false, 2, double.NaN, out _), "Two tables that hold the item stay ambiguous.");
        var summary = tracker.Drain(200);
        Check(summary.PerceivedCount == 1 && summary.Foreign == 1 && summary.Ambiguous == 1,
            "Single, foreign and ambiguous are three separate outcomes.");
        var witness = summary.Witnesses[0];
        Check(witness.SourcePrefab == 200 && witness.DropPrefab == 2 && witness.SourceKind == 5 &&
              witness.CandidatesBefore == 2 && witness.CandidatesAfterTable == 1 && witness.DistanceMetres == 1,
            "The witness records the surviving source and the filter's before/after counts.");
    }

    private static void KindRadiusIsMeasuredFromTheSource()
    {
        var tracker = Tables(radius: 12);
        tracker.Destroyed(0, 0, 0, 100, 100, 5, false, 4);
        Check(tracker.Arrived(1, 5, 0, 0, 150), "Inside the arrival radius the arrival is kept.");
        Check(!tracker.Created(1, 5, 0, 0, 160, false, 1, double.NaN, out _), "Beyond the area's own radius the source cannot have spawned it.");
        tracker.Arrived(2, 0, 3.9, 0, 150);
        Check(tracker.Created(2, 0, 3.9, 0, 160, false, 1, double.NaN, out _), "Within the area radius it is timed.");
        tracker.Destroyed(100, 0, 0, 100, 100, 5, false, 40);
        tracker.Arrived(3, 100, 11, 0, 150);
        Check(tracker.Created(3, 100, 11, 0, 160, false, 1, double.NaN, out _), "A kind radius larger than the arrival radius is clamped, never widened.");
        var summary = tracker.Drain(200);
        Check(summary.OutOfRadius == 1 && summary.PerceivedCount == 2, "Out of radius is its own outcome.");
    }

    private static void FifthCandidateMakesTheArrivalAmbiguous()
    {
        var tracker = Tables();
        for (int i = 0; i < 5; i++) tracker.Destroyed(i * 0.5, 0, 0, 100, i == 0 ? 100 : 200, 5, false, 4);
        tracker.Arrived(1, 0, 0, 0, 150);
        Check(!tracker.Created(1, 0, 0, 0, 160, false, 1, double.NaN, out _),
            "The one table match is among the four kept, but a dropped fifth candidate forbids calling it single.");
        var summary = tracker.Drain(200);
        Check(summary.CandidatesTruncated == 1 && summary.Ambiguous == 1 && summary.PerceivedCount == 0,
            "Truncation is counted and excluded from timings.");
    }

    private static void WitnessesKeepTheSlowestWithinBounds()
    {
        var tracker = Tables(window: 100000);
        // Ten timed matches, three over one second.
        double[] perceived = { 20, 900, 1500, 30, 3000, 40, 2000, 50, 60, 70 };
        for (int i = 0; i < perceived.Length; i++)
        {
            tracker.Destroyed(100 * i, 0, 0, 0, 100, 5, false, 4);
            tracker.Arrived(i, 100 * i, 0, 0, perceived[i] - 5);
            Check(tracker.Created(i, 100 * i, 0, 0, perceived[i], false, 1, double.NaN, out _), "Each isolated drop is timed.");
        }
        var summary = tracker.Drain(5000);
        Check(summary.PerceivedOverOneSecond == 3 && summary.WitnessOverflow == 0, "Three matches exceed one second.");
        Check(summary.Witnesses.Length == 4, "The four slowest are exported when fewer than four exceed a second.");
        Check(summary.Witnesses[0].PerceivedMs == 3000 && summary.Witnesses[1].PerceivedMs == 2000 &&
              summary.Witnesses[2].PerceivedMs == 1500 && summary.Witnesses[3].PerceivedMs == 900,
            "Witnesses are sorted slowest first.");
        Check(summary.Witnesses[0].NetworkMs == 2995 && summary.Witnesses[0].CreationMs == 5, "Each witness splits its legs.");

        // Away from the first batch, whose destructions are still inside the long window.
        for (int i = 0; i < 11; i++)
        {
            tracker.Destroyed(100 * i, 0, 1000, 10000, 100, 5, false, 4);
            tracker.Arrived(100 + i, 100 * i, 0, 1000, 10001);
            tracker.Created(100 + i, 100 * i, 0, 1000, 11100 + i, false, 1, double.NaN, out _);
        }
        var slow = tracker.Drain(20000);
        Check(slow.Witnesses.Length == LootVisibilityTracker<int>.WitnessCapacity && slow.WitnessOverflow == 3,
            "Every match over a second is exported up to the bound; the rest are counted.");
        Check(slow.Witnesses[0].PerceivedMs == 1110 && slow.Witnesses[7].PerceivedMs == 1103, "The bound keeps the slowest.");
        Check(tracker.Drain(20001).Witnesses.Length == 0, "Drain resets the witnesses.");
    }

    private static void OwnerLegMatchesOnlyItsOwnSingleSource()
    {
        var tracker = Tables();
        tracker.Destroyed(0, 0, 0, 100, 100, 5, owned: true, 4);
        tracker.Created(1, 0.2, 0, 0, 100.5, true, 1, double.NaN, out bool matched);
        Check(matched, "Our own area's drop, spawned in the same frame, starts the owner leg.");
        tracker.Created(2, 0.2, 0, 0, 101, true, 2, double.NaN, out matched);
        Check(!matched, "A drop our source's table cannot spawn is not its drop.");
        tracker.Destroyed(1, 0, 0, 102, 100, 5, owned: true, 4);
        tracker.Created(3, 0.5, 0, 0, 103, true, 1, double.NaN, out matched);
        Check(!matched, "Two of our own sources that both qualify are ambiguous.");
        tracker.Destroyed(40, 0, 0, 104, 100, 5, owned: false, 4);
        tracker.Created(4, 40, 0, 0, 105, true, 1, double.NaN, out matched);
        Check(!matched, "A remote destruction is never the owner leg's source.");
        var summary = tracker.Drain(200);
        Check(summary.OwnerInstantiateCount == 1 && summary.OwnerInstantiateSumMs == 0.5,
            "Destruction to Instantiate is timed once.");
        Check(summary.OwnerUnmatched == 2 && summary.OwnerAmbiguous == 1 && summary.PerceivedCount == 0,
            "Owner outcomes never enter the observer's timings.");
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

    private static void AmbiguousDestructionsAreExcluded()
    {
        var tracker = Tracker(radius: 10);
        tracker.Destroyed(0, 0, 0, 100);
        tracker.Destroyed(5, 0, 0, 200);
        Check(tracker.Arrived(1, 4, 0, 0, 250), "Keep a bounded arrival even when the source is ambiguous.");
        Check(!tracker.Created(1, 4, 0, 0, 300, false), "A nearest neighbour is not proof when several destructions qualify.");
        var summary = tracker.Drain(300);
        Check(summary.PerceivedCount == 0 && summary.Ambiguous == 1, "Ambiguous observations must not enter any latency distribution.");
        Check(summary.PendingDestructions == 2, "Attribution must not consume a destruction; one chunk drops several items.");
    }

    private static void ArrivalKeepsItsOriginalDestruction()
    {
        var tracker = new LootVisibilityTracker<int>(1, 4, 10, 5000);
        tracker.Destroyed(0, 0, 0, 100);
        tracker.Arrived(1, 0, 0, 0, 150);
        tracker.Destroyed(0, 0, 0, 180); // overwrites the original ring slot
        Check(tracker.Created(1, 0, 0, 0, 200, false), "A later destruction must not replace the source observed at arrival.");
        var summary = tracker.Drain(200);
        Check(summary.NetworkSumMs == 50 && summary.CreationSumMs == 50 && summary.PerceivedSumMs == 100,
            "Freeze t0 at arrival, including across ring overwrite; both legs must equal total elapsed time.");
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

    private static void MissingArrivalCannotInventLatency()
    {
        var tracker = Tracker();
        tracker.Destroyed(0, 0, 0, 100);
        Check(!tracker.Created(1, 0, 0, 0, 150, true), "A local drop may precede its own destruction; do not match it to an old nearby event.");
        var summary = tracker.Drain(200);
        Check(summary.ArrivalMissing == 1 && summary.LocallyOwned == 1, "Track missing arrival separately from observed ownership.");
        Check(summary.NetworkCount == 0 && summary.CreationCount == 0 && summary.PerceivedCount == 0,
            "An absent arrival must not invent any of the three latency legs.");
    }

    private static void BucketBoundariesAreInclusiveUpperBounds()
    {
        var tracker = Tracker(window: 100000);
        double[] durations = { 16, 16.001, 32, 64, 128, 256, 512, 1024, 1024.001, 50000 };
        for (int i = 0; i < durations.Length; i++)
        {
            tracker.Destroyed(100 * i, 0, 0, 0);
            tracker.Arrived(i, 100 * i, 0, 0, 0);
            tracker.Created(i, 100 * i, 0, 0, durations[i], false);
        }
        var summary = tracker.Drain(2000);
        Check(summary.PerceivedBuckets.Length == LootVisibilityTracker<int>.BucketCount, "One histogram of eight buckets.");
        Check(summary.PerceivedBuckets[0] == 1, "An exact upper bound belongs to its own bucket.");
        Check(summary.PerceivedBuckets[1] == 2, "Just over a bound moves to the next bucket.");
        for (int i = 2; i < 7; i++) Check(summary.PerceivedBuckets[i] == 1, "Each intermediate bound fills its own bucket.");
        Check(summary.PerceivedBuckets[7] == 2, "The overflow bucket holds everything above the last bound.");
    }

    private static void BackwardClockExcludesInvalidDuration()
    {
        var tracker = Tracker();
        tracker.Destroyed(0, 0, 0, 100);
        tracker.Arrived(1, 0, 0, 0, 150);
        Check(!tracker.Created(1, 0, 0, 0, 120, false), "A creation before its arrival must be excluded, not reported as fast.");
        var summary = tracker.Drain(200);
        Check(summary.NonMonotonic == 1 && summary.PerceivedCount == 0 && summary.NetworkCount == 0,
            "Count invalid chronology and keep it out of all timing totals.");
    }

    private static void DrainResetsTheIntervalAndClearCensors()
    {
        var tracker = Tracker();
        tracker.Destroyed(0, 0, 0, 100);
        tracker.Arrived(1, 0, 0, 0, 120);
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
        Check(!tracker.Created(5, 0, 0, 0, 12010, false), "An expired arrival cannot be joined to a new destruction.");
        var after = tracker.Drain(12020);
        Check(after.PerceivedCount == 0 && after.NetworkCount == 0 && after.ArrivalMissing == 1,
            "A dropped arrival is reported as missing, never as a stale network duration.");
    }
}
