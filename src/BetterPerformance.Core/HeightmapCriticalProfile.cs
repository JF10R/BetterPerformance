using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    // Which clause of the budget's critical test held, checked in the test's own order.
    public enum HeightmapCriticalClause : byte { Player, Camera, DistantLod }

    public enum HeightmapRebuildContext : byte { None, Teleporting, AfterArrival }

    // The critical rebuilds the budget ran, per capture interval: distance from the player to the
    // heightmap square (the critical test's metric), cost, same-frame pile-up and context.
    // Observation only: it takes copies of values and feeds nothing back. docs/heightmap-rebuild-budget.md.
    public sealed class HeightmapCriticalProfile
    {
        public const string Prefix = "heightmap_budget_critical_";
        public const double FreshEnableMs = 1000, RecentEnableMs = 10000;
        public static readonly string[] Buckets = { "on_square", "0_16", "16_32", "32_48", "48_64", "64_80", "80_plus" };
        private static readonly string[] Contexts = { "ctx_none", "ctx_teleporting", "ctx_after_arrival" };
        private static readonly string[] Ages = { "age_1s", "age_10s", "age_older", "age_unknown" };

        private readonly long[] bucketCount = new long[7];
        private readonly double[] bucketSum = new double[7], bucketMax = new double[7];
        private readonly long[] clauseCount = new long[3];
        private readonly long[] contextCount = new long[3], ageCount = new long[4];
        private readonly double[] contextSum = new double[3], ageSum = new double[4];
        private long unplanned, frames;
        private int frame = -1, frameCount, frameCountMax, worstCount;
        private double frameMs, worstMs;

        // Upper edges inclusive, as the critical test's own (<= radius): 16 m is 0_16, 80 m is 64_80.
        public static int Bucket(double gapM)
        {
            if (gapM <= 0) return 0;
            for (int i = 1; i < 6; i++) if (gapM <= 16 * i) return i;
            return 6; // beyond 80 m, or NaN
        }

        // An inexact age is a lower bound (enabled before everything the log retains).
        public static int AgeBucket(double ageMs, bool exact)
        {
            if (!exact) return ageMs > RecentEnableMs ? 2 : 3;
            return ageMs <= FreshEnableMs ? 0 : ageMs <= RecentEnableMs ? 1 : 2;
        }

        public void Observe(int frameIndex, double gapM, double elapsedMs, HeightmapCriticalClause clause,
            HeightmapRebuildContext context, double enableAgeMs, bool ageExact, bool planned)
        {
            double ms = double.IsNaN(elapsedMs) || elapsedMs < 0 ? 0 : elapsedMs;
            if (frameIndex != frame) { CloseFrame(); frame = frameIndex; frames++; }
            frameCount++;
            frameMs += ms;
            int bucket = Bucket(gapM);
            bucketCount[bucket]++;
            bucketSum[bucket] += ms;
            if (ms > bucketMax[bucket]) bucketMax[bucket] = ms;
            clauseCount[Math.Min((int)clause, 2)]++;
            int c = Math.Min((int)context, 2);
            contextCount[c]++;
            contextSum[c] += ms;
            int age = AgeBucket(enableAgeMs, ageExact);
            ageCount[age]++;
            ageSum[age] += ms;
            if (!planned) unplanned++;
        }

        private void CloseFrame()
        {
            if (frameCount > frameCountMax) frameCountMax = frameCount;
            if (frameMs > worstMs) { worstMs = frameMs; worstCount = frameCount; }
            frameCount = 0;
            frameMs = 0;
        }

        // Folds the open frame in (sampling never runs inside the late batch), exports, resets.
        public void Sample(List<NumberValue> gauges)
        {
            CloseFrame();
            frame = -1;
            for (int i = 0; i < Buckets.Length; i++)
            {
                gauges.Add(new NumberValue(Prefix + Buckets[i] + "_count", bucketCount[i], "rebuilds"));
                gauges.Add(new NumberValue(Prefix + Buckets[i] + "_ms_sum", Math.Round(bucketSum[i], 3), "ms"));
                gauges.Add(new NumberValue(Prefix + Buckets[i] + "_ms_max", Math.Round(bucketMax[i], 3), "ms"));
            }
            gauges.Add(new NumberValue(Prefix + "unplanned", unplanned, "rebuilds"));
            gauges.Add(new NumberValue(Prefix + "frames", frames, "frames"));
            gauges.Add(new NumberValue(Prefix + "frame_count_max", frameCountMax, "rebuilds"));
            gauges.Add(new NumberValue(Prefix + "worst_frame_ms", Math.Round(worstMs, 3), "ms"));
            gauges.Add(new NumberValue(Prefix + "worst_frame_count", worstCount, "rebuilds"));
            gauges.Add(new NumberValue(Prefix + "by_player", clauseCount[0], "rebuilds"));
            gauges.Add(new NumberValue(Prefix + "by_camera", clauseCount[1], "rebuilds"));
            gauges.Add(new NumberValue(Prefix + "by_distant_lod", clauseCount[2], "rebuilds"));
            for (int i = 0; i < Contexts.Length; i++)
            {
                gauges.Add(new NumberValue(Prefix + Contexts[i] + "_count", contextCount[i], "rebuilds"));
                gauges.Add(new NumberValue(Prefix + Contexts[i] + "_ms_sum", Math.Round(contextSum[i], 3), "ms"));
            }
            for (int i = 0; i < Ages.Length; i++)
            {
                gauges.Add(new NumberValue(Prefix + Ages[i] + "_count", ageCount[i], "rebuilds"));
                gauges.Add(new NumberValue(Prefix + Ages[i] + "_ms_sum", Math.Round(ageSum[i], 3), "ms"));
            }
            Reset();
        }

        public void Reset()
        {
            Array.Clear(bucketCount, 0, bucketCount.Length);
            Array.Clear(bucketSum, 0, bucketSum.Length);
            Array.Clear(bucketMax, 0, bucketMax.Length);
            Array.Clear(clauseCount, 0, clauseCount.Length);
            Array.Clear(contextCount, 0, contextCount.Length);
            Array.Clear(contextSum, 0, contextSum.Length);
            Array.Clear(ageCount, 0, ageCount.Length);
            Array.Clear(ageSum, 0, ageSum.Length);
            unplanned = frames = 0;
            frame = -1;
            frameCount = frameCountMax = worstCount = 0;
            frameMs = worstMs = 0;
        }
    }

    // The last enables of heightmaps (zone loads), newest overwriting oldest; keyed by instance id.
    public sealed class HeightmapEnableLog
    {
        private readonly int[] ids;
        private readonly long[] stamps;
        private int next, count;

        public HeightmapEnableLog(int capacity = 256)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            ids = new int[capacity];
            stamps = new long[capacity];
        }

        public void Note(int id, long timestamp)
        {
            ids[next] = id;
            stamps[next] = timestamp;
            next = (next + 1) % ids.Length;
            if (count < ids.Length) count++;
        }

        // Exact: since the id's last logged enable. Otherwise a lower bound: +inf while nothing
        // was evicted, else the age of the oldest retained entry.
        public double AgeMs(int id, long now, long frequency, out bool exact)
        {
            double scale = 1000.0 / Math.Max(1, frequency);
            for (int k = 1; k <= count; k++)
            {
                int i = (next - k + ids.Length) % ids.Length;
                if (ids[i] == id) { exact = true; return (now - stamps[i]) * scale; }
            }
            exact = false;
            if (count < ids.Length) return double.PositiveInfinity;
            return (now - stamps[next]) * scale; // next is the oldest slot once full
        }

        public void Clear() { next = count = 0; }
    }
}
