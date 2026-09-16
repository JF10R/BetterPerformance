using System;
using System.Linq;
using BetterPerformance.Core;

internal static class KeyedAggregatorTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        Ordering();
        Conservation();
        Overflow();
        Validation();
        ResetAndReuse();
        HotPathAllocation();
    }

    private static void Ordering()
    {
        var byTime = new KeyedAggregator("prefab_create", 8);
        byTime.Record(1, 5);
        byTime.Record(2, 20);
        byTime.Record(3, 1);
        byTime.Record(2, 5);
        var rows = byTime.Drain(2);
        Check(rows.Length == 3, "Top-N plus the synthetic remainder row must be emitted.");
        Check(rows[0].Key == "2" && rows[0].Count == 2 && rows[0].SumMs == 25 && rows[0].MaxMs == 20,
            "The heaviest key must lead and keep its own count/sum/max.");
        Check(rows[1].Key == "1" && rows[2].Key == "other", "Remaining keys collapse into one trailing row.");
        Check(rows.All(row => row.Group == "prefab_create"), "Every row carries its group.");

        var byBytes = new KeyedAggregator("prefab_send_bytes", 8, AttributionOrder.Bytes);
        byBytes.Record(1, 100, 10);
        byBytes.Record(2, 1, 900);
        var ranked = byBytes.Drain(1);
        Check(ranked[0].Key == "2" && ranked[0].Bytes == 900, "A bytes group ranks by bytes, not by elapsed time.");
        Check(ranked[1].Key == "other" && ranked[1].SumMs == 100, "The heavier-in-time key still lands in the remainder.");

        var named = new KeyedAggregator("routed_rpc", 8);
        named.Record(7, 3);
        named.Record(8, 1);
        var resolved = named.Drain(4, key => key == 7 ? "Say" : "");
        Check(resolved[0].Key == "Say" && resolved[1].Key == "8",
            "A supplied resolver names the key and an unnamed key falls back to its number.");
    }

    private static void Conservation()
    {
        var aggregator = new KeyedAggregator("prefab_create", 4);
        double expectedSum = 0;
        long expectedCount = 0;
        for (int key = 0; key < 16; key++)
            for (int sample = 1; sample <= 3; sample++)
            {
                aggregator.Record(key, key + sample);
                expectedSum += key + sample;
                expectedCount++;
            }
        var rows = aggregator.Drain(2);
        Check(rows.Length == 3, "Capacity four with top two leaves exactly one remainder row.");
        Check(rows.Sum(row => row.Count) == expectedCount, "Dropped and non-top counts must survive in the remainder.");
        Check(Math.Abs(rows.Sum(row => row.SumMs) - expectedSum) < 1e-9, "Total time must be conserved across the export.");
        Check(rows.Max(row => row.MaxMs) == 18, "The interval maximum must remain visible.");
        Check(rows.Single(row => row.Key == "other").MaxMs == 18, "Dropped keys contribute their maximum to the remainder.");
    }

    private static void Overflow()
    {
        var aggregator = new KeyedAggregator("routed_rpc", 2);
        aggregator.Record(1, 1);
        aggregator.Record(2, 1);
        Check(aggregator.TrackedKeys == 2, "Capacity is the tracked key limit.");
        aggregator.Record(3, 4, 40);
        aggregator.Record(3, 6, 60);
        Check(aggregator.TrackedKeys == 2 && aggregator.DroppedRecords == 2, "New keys past capacity are dropped and counted.");
        var rows = aggregator.Drain(8);
        var other = rows.Single(row => row.Key == "other");
        Check(other.Count == 2 && other.SumMs == 10 && other.MaxMs == 6 && other.Bytes == 100,
            "Dropped observations are reported in full, not discarded.");
        Check(aggregator.DroppedRecords == 2, "The cumulative drop counter survives an export.");
        var second = aggregator.Drain(8);
        Check(second.Length == 0, "A drained aggregator reports nothing until new samples arrive.");
    }

    private static void Validation()
    {
        var aggregator = new KeyedAggregator("prefab_create", 4);
        aggregator.Record(1, double.NaN);
        aggregator.Record(1, double.PositiveInfinity);
        aggregator.Record(1, -0.5);
        aggregator.Record(1, 1, -3);
        Check(aggregator.InvalidSamples == 4 && aggregator.TrackedKeys == 0,
            "Invalid samples are counted and never stored.");
        aggregator.Record(1, 0, 0);
        Check(aggregator.Drain(4).Single().Count == 1, "A zero-cost observation is still a valid observation.");
        Check(ThrowsRange(() => new KeyedAggregator("x", 0)), "Capacity must be positive.");
        Check(ThrowsRange(() => new KeyedAggregator("x", 8).Drain(-1)), "A negative top-N is rejected.");
    }

    private static void ResetAndReuse()
    {
        var aggregator = new KeyedAggregator("prefab_create", 2);
        aggregator.Record(1, 1);
        aggregator.Record(2, 1);
        aggregator.Record(3, 1);
        aggregator.Record(1, double.NaN);
        aggregator.Reset();
        Check(aggregator.TrackedKeys == 0 && aggregator.DroppedRecords == 0 && aggregator.InvalidSamples == 0,
            "Reset clears keys and cumulative counters.");
        Check(aggregator.Drain(4).Length == 0, "Reset leaves nothing to export.");
        aggregator.Record(9, 2);
        Check(aggregator.Drain(4).Single().Key == "9", "The aggregator is reusable after a reset.");
    }

    private static void HotPathAllocation()
    {
        var aggregator = new KeyedAggregator("prefab_create", 256);
        for (int round = 0; round < 40; round++)
            for (int key = 0; key < 256; key++) aggregator.Record(key, 1, 8);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 40; round++)
            for (int key = 0; key < 256; key++) aggregator.Record(key, 1, 8);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Keyed aggregation must allocate no managed bytes after warmup.");
        aggregator.Record(9999, 1, 8);
        long beforeDropped = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) aggregator.Record(9999 + i, 1, 8);
        Check(GC.GetAllocatedBytesForCurrentThread() == beforeDropped, "Dropping an unseen key must not allocate either.");
    }

    private static bool ThrowsRange(Action action)
    {
        try { action(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }
}
