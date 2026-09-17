using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    // A destruction and a drop are joined by position and time, never by identity: the
    // game does not link them. Every duration is therefore an attributed lower bound on
    // one process's clock, not a proven cause. Single-threaded.
    public sealed class LootVisibilityTracker<TKey> where TKey : notnull
    {
        public const int BucketCount = 8;

        // Seven upper bounds plus one overflow bucket.
        public static readonly double[] BucketUpperBoundsMs = { 16, 32, 64, 128, 256, 512, 1024 };

        private struct Destruction
        {
            internal bool Active;
            internal double X, Y, Z, AtMs;
        }

        private struct Arrival
        {
            internal double AtMs;
        }

        public struct Summary
        {
            public long NetworkCount, CreationCount, PerceivedCount;
            public double NetworkSumMs, NetworkMaxMs;
            public double CreationSumMs, CreationMaxMs;
            public double PerceivedSumMs, PerceivedMaxMs;
            public long ArrivalMissing, LocallyOwned, Unattributed, Censored;
            public long Overflowed, ArrivalCapacitySkipped, NonMonotonic, ArrivalsExpired;
            public int PendingDestructions, PendingArrivals;
            // One histogram of the perceived duration; null only on a default instance.
            public long[] PerceivedBuckets;
        }

        private readonly Destruction[] destructions;
        private readonly Dictionary<TKey, Arrival> arrivals;
        private readonly List<TKey> expired = new List<TKey>();
        private readonly long[] buckets = new long[BucketCount];
        private readonly double radiusSquared, windowMs;
        private readonly int arrivalCapacity;
        private int head, active;
        private Summary interval;

        public LootVisibilityTracker(int destructionCapacity, int arrivalCapacity, double radiusMetres, double windowMs)
        {
            if (destructionCapacity < 1 || destructionCapacity > 4096) throw new ArgumentOutOfRangeException(nameof(destructionCapacity));
            if (arrivalCapacity < 1 || arrivalCapacity > 16384) throw new ArgumentOutOfRangeException(nameof(arrivalCapacity));
            if (!(radiusMetres > 0) || double.IsInfinity(radiusMetres)) throw new ArgumentOutOfRangeException(nameof(radiusMetres));
            if (!(windowMs > 0) || double.IsInfinity(windowMs)) throw new ArgumentOutOfRangeException(nameof(windowMs));
            destructions = new Destruction[destructionCapacity];
            this.arrivalCapacity = arrivalCapacity;
            arrivals = new Dictionary<TKey, Arrival>(arrivalCapacity);
            radiusSquared = radiusMetres * radiusMetres;
            this.windowMs = windowMs;
        }

        // A maintained count, so a hot hook can leave after one field read. It counts
        // slots still occupied, including entries whose window has expired unswept.
        public int PendingDestructions => active;

        public int PendingArrivals => arrivals.Count;

        public void Destroyed(double x, double y, double z, double nowMs)
        {
            ValidateTime(nowMs);
            ValidatePoint(x, y, z);
            var replaced = destructions[head];
            if (replaced.Active && Live(replaced, nowMs)) interval.Overflowed++;
            if (!replaced.Active) active++;
            destructions[head] = new Destruction { Active = true, X = x, Y = y, Z = z, AtMs = nowMs };
            head = (head + 1) % destructions.Length;
        }

        // The prefab is not deserialized at the arrival point, so an arrival is kept only
        // when a live destruction is near it. Everything else is dropped without storage.
        public bool Arrived(TKey key, double x, double y, double z, double nowMs)
        {
            ValidateTime(nowMs);
            ValidatePoint(x, y, z);
            if (Nearest(x, y, z, nowMs) < 0) return false;
            if (arrivals.ContainsKey(key)) return true;
            if (arrivals.Count >= arrivalCapacity) { interval.ArrivalCapacitySkipped++; return false; }
            arrivals.Add(key, new Arrival { AtMs = nowMs });
            return true;
        }

        public bool Created(TKey key, double x, double y, double z, double nowMs, bool locallyOwned)
        {
            ValidateTime(nowMs);
            ValidatePoint(x, y, z);
            int slot = Nearest(x, y, z, nowMs);
            if (slot < 0)
            {
                arrivals.Remove(key);
                interval.Unattributed++;
                return false;
            }
            // One destroyed hit area drops several items, so the destruction stays live
            // until the window expires instead of being consumed by the first drop.
            double destroyedAt = destructions[slot].AtMs;
            double perceived = Clamp(nowMs - destroyedAt);
            interval.PerceivedCount++;
            interval.PerceivedSumMs += perceived;
            interval.PerceivedMaxMs = Math.Max(interval.PerceivedMaxMs, perceived);
            buckets[Bucket(perceived)]++;
            if (arrivals.TryGetValue(key, out var arrival))
            {
                arrivals.Remove(key);
                double network = Clamp(arrival.AtMs - destroyedAt);
                double creation = Clamp(nowMs - arrival.AtMs);
                interval.NetworkCount++;
                interval.NetworkSumMs += network;
                interval.NetworkMaxMs = Math.Max(interval.NetworkMaxMs, network);
                interval.CreationCount++;
                interval.CreationSumMs += creation;
                interval.CreationMaxMs = Math.Max(interval.CreationMaxMs, creation);
            }
            else interval.ArrivalMissing++;
            if (locallyOwned) interval.LocallyOwned++;
            return true;
        }

        public Summary Drain(double nowMs)
        {
            ValidateTime(nowMs);
            Expire(nowMs);
            var summary = interval;
            summary.PendingDestructions = active;
            summary.PendingArrivals = arrivals.Count;
            summary.PerceivedBuckets = (long[])buckets.Clone();
            interval = default;
            Array.Clear(buckets, 0, buckets.Length);
            return summary;
        }

        // A scene change censors pending state. A new capture resets all counters instead,
        // so observations from independent captures cannot mix.
        public void Clear(bool censorPending)
        {
            if (censorPending) interval.Censored += active + arrivals.Count;
            else
            {
                interval = default;
                Array.Clear(buckets, 0, buckets.Length);
            }
            Array.Clear(destructions, 0, destructions.Length);
            arrivals.Clear();
            head = active = 0;
        }

        public void Expire(double nowMs)
        {
            ValidateTime(nowMs);
            for (int i = 0; i < destructions.Length; i++)
                if (destructions[i].Active && !Live(destructions[i], nowMs)) { destructions[i] = default; active--; }
            // An arrival is stored before the prefab is known, so most of them are never
            // claimed by Created: only a loot prefab reaches it. Without this sweep the
            // map fills with unclaimed entries and silently stops recording the network
            // leg. Past the window it can no longer match a live destruction anyway.
            if (arrivals.Count == 0) return;
            expired.Clear();
            foreach (var entry in arrivals)
                if (nowMs < entry.Value.AtMs || nowMs - entry.Value.AtMs >= windowMs) expired.Add(entry.Key);
            foreach (var key in expired) arrivals.Remove(key);
            interval.ArrivalsExpired += expired.Count;
            expired.Clear();
        }

        private int Nearest(double x, double y, double z, double nowMs)
        {
            int best = -1;
            double bestDistance = radiusSquared;
            for (int i = 0; i < destructions.Length; i++)
            {
                var entry = destructions[i];
                if (!entry.Active || !Live(entry, nowMs)) continue;
                double dx = entry.X - x, dy = entry.Y - y, dz = entry.Z - z;
                double distance = dx * dx + dy * dy + dz * dz;
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = i;
            }
            return best;
        }

        private bool Live(Destruction entry, double nowMs) => nowMs >= entry.AtMs && nowMs - entry.AtMs < windowMs;

        private double Clamp(double duration)
        {
            if (duration >= 0) return duration;
            interval.NonMonotonic++;
            return 0;
        }

        private static int Bucket(double durationMs)
        {
            for (int i = 0; i < BucketUpperBoundsMs.Length; i++)
                if (durationMs <= BucketUpperBoundsMs[i]) return i;
            return BucketCount - 1;
        }

        private static void ValidateTime(double nowMs)
        {
            if (double.IsNaN(nowMs) || double.IsInfinity(nowMs)) throw new ArgumentOutOfRangeException(nameof(nowMs));
        }

        private static void ValidatePoint(double x, double y, double z)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) throw new ArgumentOutOfRangeException(nameof(x));
            if (double.IsNaN(y) || double.IsInfinity(y)) throw new ArgumentOutOfRangeException(nameof(y));
            if (double.IsNaN(z) || double.IsInfinity(z)) throw new ArgumentOutOfRangeException(nameof(z));
        }
    }
}
