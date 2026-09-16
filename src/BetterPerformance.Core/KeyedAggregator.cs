using System;
using System.Collections.Generic;
using System.Globalization;

namespace BetterPerformance.Core
{
    public enum AttributionOrder { SumMs, Bytes }

    // Folds two identifier hashes into one aggregation key so a group can be split by a
    // second dimension without a second aggregator. Mix is lossy, so callers keep the
    // packed pair in a bounded side map and name the key from there at export.
    public static class CompositeKey
    {
        public const string NoTarget = "(none)";
        public const string Separator = "→";

        public static int Mix(int owner, int target) => unchecked((owner * 486187739) ^ target);
        public static long Pack(int owner, int target) => ((long)(uint)owner << 32) | (uint)target;
        public static int Owner(long packed) => (int)(packed >> 32);
        public static int Target(long packed) => unchecked((int)(uint)packed);
        public static string Name(string owner, string target) =>
            (string.IsNullOrEmpty(owner) ? "?" : owner) + Separator +
            (string.IsNullOrEmpty(target) ? NoTarget : target);
    }

    // Bounded per-key aggregation for one attribution group. Capacity is fixed: once
    // it is reached, further unseen keys are dropped and counted instead of growing.
    // Record allocates no managed bytes once the key set is warm.
    public sealed class KeyedAggregator
    {
        private readonly object gate = new object();
        private readonly Dictionary<int, int> slots;
        private readonly int[] keys;
        private readonly long[] counts;
        private readonly double[] sums;
        private readonly double[] maxima;
        private readonly long[] byteTotals;
        private readonly AttributionOrder order;
        private int used;
        private long droppedRecords, droppedBytes;
        private double droppedSum, droppedMax;
        private long droppedRecordsTotal, invalidTotal;

        public string Group { get; }
        public int Capacity { get; }
        public int TrackedKeys { get { lock (gate) { return used; } } }
        // Cumulative since construction or Reset; Drain does not clear these.
        public long DroppedRecords { get { lock (gate) { return droppedRecordsTotal; } } }
        public long InvalidSamples { get { lock (gate) { return invalidTotal; } } }

        public KeyedAggregator(string group, int capacity = 256, AttributionOrder order = AttributionOrder.SumMs)
        {
            if (string.IsNullOrEmpty(group)) throw new ArgumentException("A group name is required.", nameof(group));
            if (capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (order != AttributionOrder.SumMs && order != AttributionOrder.Bytes) throw new ArgumentOutOfRangeException(nameof(order));
            Group = group;
            Capacity = capacity;
            this.order = order;
            slots = new Dictionary<int, int>(capacity);
            keys = new int[capacity];
            counts = new long[capacity];
            sums = new double[capacity];
            maxima = new double[capacity];
            byteTotals = new long[capacity];
        }

        // Non-finite, negative durations and negative byte counts are counted as
        // invalid and never stored: a corrupt sample must not distort a total.
        public void Record(int key, double ms, long bytes = 0)
        {
            lock (gate)
            {
                if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0 || bytes < 0)
                {
                    invalidTotal++;
                    return;
                }
                if (!slots.TryGetValue(key, out int slot))
                {
                    if (used == Capacity)
                    {
                        droppedRecords++;
                        droppedRecordsTotal++;
                        droppedSum += ms;
                        droppedBytes += bytes;
                        if (ms > droppedMax) droppedMax = ms;
                        return;
                    }
                    slot = used++;
                    keys[slot] = key;
                    slots[key] = slot;
                }
                counts[slot]++;
                sums[slot] += ms;
                byteTotals[slot] += bytes;
                if (ms > maxima[slot]) maxima[slot] = ms;
            }
        }

        // Returns the top-N rows by the configured order plus a single synthetic
        // "other" row holding every remaining and every dropped observation, so the
        // emitted rows still sum to the interval total. Clears the interval state.
        public AttributionSummary[] Drain(int topN, Func<int, string>? resolveKey = null)
        {
            if (topN < 0) throw new ArgumentOutOfRangeException(nameof(topN));
            lock (gate)
            {
                int tracked = used;
                var ordered = new int[tracked];
                for (int i = 0; i < tracked; i++) ordered[i] = i;
                Array.Sort(ordered, Compare);
                int take = Math.Min(topN, tracked);
                var rows = new List<AttributionSummary>(take + 1);
                long otherCount = droppedRecords, otherBytes = droppedBytes;
                double otherSum = droppedSum, otherMax = droppedMax;
                for (int i = 0; i < tracked; i++)
                {
                    int slot = ordered[i];
                    if (i < take)
                    {
                        rows.Add(new AttributionSummary
                        {
                            Group = Group,
                            Key = Name(resolveKey, keys[slot]),
                            Count = counts[slot],
                            SumMs = sums[slot],
                            MaxMs = maxima[slot],
                            Bytes = byteTotals[slot]
                        });
                        continue;
                    }
                    otherCount += counts[slot];
                    otherSum += sums[slot];
                    otherBytes += byteTotals[slot];
                    if (maxima[slot] > otherMax) otherMax = maxima[slot];
                }
                if (otherCount > 0)
                    rows.Add(new AttributionSummary
                    {
                        Group = Group,
                        Key = "other",
                        Count = otherCount,
                        SumMs = otherSum,
                        MaxMs = otherMax,
                        Bytes = otherBytes
                    });
                ClearInterval(tracked);
                return rows.ToArray();
            }
        }

        public void Reset()
        {
            lock (gate)
            {
                ClearInterval(used);
                droppedRecordsTotal = 0;
                invalidTotal = 0;
            }
        }

        private void ClearInterval(int tracked)
        {
            for (int i = 0; i < tracked; i++)
            {
                keys[i] = 0;
                counts[i] = 0;
                sums[i] = 0;
                maxima[i] = 0;
                byteTotals[i] = 0;
            }
            used = 0;
            slots.Clear();
            droppedRecords = 0;
            droppedBytes = 0;
            droppedSum = 0;
            droppedMax = 0;
        }

        // Descending by the configured order; equal weights fall back to the key so
        // repeated exports of the same data produce the same rows.
        private int Compare(int left, int right)
        {
            int comparison = order == AttributionOrder.Bytes
                ? byteTotals[right].CompareTo(byteTotals[left])
                : sums[right].CompareTo(sums[left]);
            if (comparison != 0) return comparison;
            comparison = sums[right].CompareTo(sums[left]);
            if (comparison != 0) return comparison;
            return keys[left].CompareTo(keys[right]);
        }

        private static string Name(Func<int, string>? resolveKey, int key)
        {
            string? resolved = resolveKey == null ? null : resolveKey(key);
            return string.IsNullOrEmpty(resolved) ? key.ToString(CultureInfo.InvariantCulture) : resolved!;
        }
    }
}
