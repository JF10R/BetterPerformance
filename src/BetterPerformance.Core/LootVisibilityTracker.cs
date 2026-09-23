using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    // A destruction and a drop are joined by position and time, never by identity: the
    // game does not link them. v3 freezes up to four candidates at arrival, excluding any
    // source this process owned (its drops are local, never network arrivals). At creation
    // only candidates whose drop table holds the item and whose own radius covers it are
    // kept; exactly one left is timed. Single-threaded.
    public sealed class LootVisibilityTracker<TKey> where TKey : notnull
    {
        public const int BucketCount = 8;
        public const int MaxCandidates = 4;
        public const int WitnessCapacity = 8;
        public const int WitnessSlowest = 4;
        public const double SlowMs = 1000;
        // Spawn age is read on the synced game clock, re-set from the server every 2 s;
        // the margin keeps that offset from excluding a genuine new drop.
        public const double StaleMarginMs = 3000;

        // Seven upper bounds plus one overflow bucket.
        public static readonly double[] BucketUpperBoundsMs = { 16, 32, 64, 128, 256, 512, 1024 };

        private static readonly Comparison<Witness> Slowest = (a, b) => b.PerceivedMs.CompareTo(a.PerceivedMs);

        private struct Destruction
        {
            internal bool Active, Owned;
            internal double X, Y, Z, AtMs, Radius;
            internal int Source;
            internal byte Kind;
        }

        private struct Candidate
        {
            internal double X, Y, Z, AtMs, Radius, DistanceSquared;
            internal int Source;
            internal byte Kind;
        }

        // Candidates inline so an arrival allocates nothing beyond its dictionary slot.
        private struct Arrival
        {
            internal double AtMs, X, Y, Z;
            internal int Count, Owned;
            internal bool Truncated;
            internal Candidate C0, C1, C2, C3;
        }

        public struct Witness
        {
            public int DropPrefab, SourcePrefab;
            public byte SourceKind;
            public double DistanceMetres, NetworkMs, CreationMs, PerceivedMs;
            public int CandidatesBefore, CandidatesAfterTable, OwnedExcluded;
        }

        public struct Summary
        {
            public long NetworkCount, CreationCount, PerceivedCount;
            public double NetworkSumMs, NetworkMaxMs;
            public double CreationSumMs, CreationMaxMs;
            public double PerceivedSumMs, PerceivedMaxMs;
            public long ArrivalMissing, LocallyOwned, Unattributed, Censored, Ambiguous;
            public long Overflowed, ArrivalCapacitySkipped, NonMonotonic, ArrivalsExpired;
            // v3 outcomes, all excluded from timings.
            public long Foreign, OutOfRadius, OwnedOnly, OwnedSourceExcluded, CandidatesTruncated, Stale;
            public long PerceivedOverOneSecond, WitnessOverflow;
            // Owner side: own destruction to own Instantiate of a matching drop.
            public long OwnerInstantiateCount, OwnerUnmatched, OwnerAmbiguous;
            public double OwnerInstantiateSumMs, OwnerInstantiateMaxMs;
            public int PendingDestructions, PendingArrivals;
            // One histogram of the perceived duration; null only on a default instance.
            public long[] PerceivedBuckets;
            // Slowest timed matches, slowest first; null only on a default instance.
            public Witness[] Witnesses;
        }

        private readonly Destruction[] destructions;
        private readonly Dictionary<TKey, Arrival> arrivals;
        private readonly List<TKey> expired = new List<TKey>();
        private readonly long[] buckets = new long[BucketCount];
        private readonly Witness[] witnesses = new Witness[WitnessCapacity];
        private readonly Func<int, int, bool>? canSpawn;
        private readonly double radius, radiusSquared, windowMs;
        private readonly int arrivalCapacity;
        private int head, active, witnessCount;
        private Summary interval;

        // canSpawn(sourcePrefab, dropPrefab) answers the drop-table question; null accepts
        // every pair, which is the v2 rule and what a source without a table relies on.
        public LootVisibilityTracker(int destructionCapacity, int arrivalCapacity, double radiusMetres, double windowMs,
            Func<int, int, bool>? canSpawn = null)
        {
            if (destructionCapacity < 1 || destructionCapacity > 4096) throw new ArgumentOutOfRangeException(nameof(destructionCapacity));
            if (arrivalCapacity < 1 || arrivalCapacity > 16384) throw new ArgumentOutOfRangeException(nameof(arrivalCapacity));
            if (!(radiusMetres > 0) || double.IsInfinity(radiusMetres)) throw new ArgumentOutOfRangeException(nameof(radiusMetres));
            if (!(windowMs > 0) || double.IsInfinity(windowMs)) throw new ArgumentOutOfRangeException(nameof(windowMs));
            destructions = new Destruction[destructionCapacity];
            this.arrivalCapacity = arrivalCapacity;
            arrivals = new Dictionary<TKey, Arrival>(arrivalCapacity);
            radius = radiusMetres;
            radiusSquared = radiusMetres * radiusMetres;
            this.windowMs = windowMs;
            this.canSpawn = canSpawn;
        }

        // A maintained count, so a hot hook can leave after one field read. It counts
        // slots still occupied, including entries whose window has expired unswept.
        public int PendingDestructions => active;

        public int PendingArrivals => arrivals.Count;

        public void Destroyed(double x, double y, double z, double nowMs) =>
            Destroyed(x, y, z, nowMs, 0, 0, false, radius);

        // radiusMetres is the source kind's own spawn spread, clamped to the arrival radius.
        public void Destroyed(double x, double y, double z, double nowMs, int sourcePrefab, byte kind, bool owned, double radiusMetres)
        {
            ValidateTime(nowMs);
            ValidatePoint(x, y, z);
            if (!(radiusMetres > 0)) throw new ArgumentOutOfRangeException(nameof(radiusMetres));
            var replaced = destructions[head];
            if (replaced.Active && Live(replaced, nowMs)) interval.Overflowed++;
            if (!replaced.Active) active++;
            destructions[head] = new Destruction
            {
                Active = true, Owned = owned, X = x, Y = y, Z = z, AtMs = nowMs,
                Radius = Math.Min(radiusMetres, radius), Source = sourcePrefab, Kind = kind
            };
            head = (head + 1) % destructions.Length;
        }

        // The prefab is not deserialized at the arrival point, so an arrival is kept only
        // when a live destruction is near it. Everything else is dropped without storage.
        public bool Arrived(TKey key, double x, double y, double z, double nowMs)
        {
            ValidateTime(nowMs);
            ValidatePoint(x, y, z);
            if (arrivals.ContainsKey(key)) return true;
            var arrival = new Arrival { AtMs = nowMs, X = x, Y = y, Z = z };
            for (int i = 0; i < destructions.Length; i++)
            {
                var entry = destructions[i];
                if (!entry.Active || !Live(entry, nowMs)) continue;
                double distance = DistanceSquared(entry.X, entry.Y, entry.Z, x, y, z);
                if (distance > radiusSquared) continue;
                // Its drops were instantiated here, so it cannot be this arrival's source.
                if (entry.Owned) { arrival.Owned++; continue; }
                Keep(ref arrival, new Candidate
                {
                    X = entry.X, Y = entry.Y, Z = entry.Z, AtMs = entry.AtMs, Radius = entry.Radius,
                    DistanceSquared = distance, Source = entry.Source, Kind = entry.Kind
                });
            }
            if (arrival.Count == 0 && arrival.Owned == 0) return false;
            if (arrivals.Count >= arrivalCapacity) { interval.ArrivalCapacitySkipped++; return false; }
            arrivals.Add(key, arrival);
            return true;
        }

        public bool Created(TKey key, double x, double y, double z, double nowMs, bool locallyOwned) =>
            Created(key, x, y, z, nowMs, locallyOwned, 0, double.NaN, out _);

        // spawnAgeMs: the drop's age by its own spawn stamp, NaN when unknown; an old drop
        // re-entering view is a first arrival here but not a new drop. ownerMatched: a drop
        // this process instantiated matched exactly one destruction it performed itself.
        public bool Created(TKey key, double x, double y, double z, double nowMs, bool locallyOwned, int dropPrefab,
            double spawnAgeMs, out bool ownerMatched)
        {
            ownerMatched = false;
            ValidateTime(nowMs);
            ValidatePoint(x, y, z);
            if (!arrivals.TryGetValue(key, out var arrival))
            {
                if (Nearest(x, y, z, nowMs) < 0) interval.Unattributed++;
                else
                {
                    // Locally spawned drops can precede the source's Destroy call.
                    // An old nearby destruction must not manufacture a latency for them.
                    // Missing arrival can also mean a skipped/expired network observation.
                    interval.ArrivalMissing++;
                    if (locallyOwned)
                    {
                        interval.LocallyOwned++;
                        ownerMatched = MatchOwner(x, y, z, nowMs, dropPrefab);
                    }
                }
                return false;
            }
            arrivals.Remove(key);
            if (locallyOwned) interval.LocallyOwned++;
            if (nowMs < arrival.AtMs) { interval.NonMonotonic++; return false; }
            if (spawnAgeMs > windowMs + StaleMarginMs) { interval.Stale++; return false; }
            if (arrival.Owned > 0) interval.OwnedSourceExcluded++;
            if (arrival.Count == 0) { interval.OwnedOnly++; return false; }
            int live = 0, table = 0, near = 0;
            Candidate match = default;
            for (int i = 0; i < arrival.Count; i++)
            {
                var candidate = Get(arrival, i);
                if (nowMs - candidate.AtMs >= windowMs) continue;
                live++;
                if (canSpawn != null && !canSpawn(candidate.Source, dropPrefab)) continue;
                table++;
                if (candidate.DistanceSquared > candidate.Radius * candidate.Radius) continue;
                near++;
                match = candidate;
            }
            if (live == 0) { interval.Unattributed++; return false; }
            // A fifth candidate was not kept, so no single survivor is proof.
            if (arrival.Truncated) { interval.CandidatesTruncated++; interval.Ambiguous++; return false; }
            if (table == 0) { interval.Foreign++; return false; }
            if (near == 0) { interval.OutOfRadius++; return false; }
            if (near > 1) { interval.Ambiguous++; return false; }
            // One destroyed hit area drops several items, so the destruction stays live
            // until the window expires instead of being consumed by the first drop.
            double perceived = nowMs - match.AtMs;
            interval.PerceivedCount++;
            interval.PerceivedSumMs += perceived;
            interval.PerceivedMaxMs = Math.Max(interval.PerceivedMaxMs, perceived);
            buckets[Bucket(perceived)]++;
            double network = arrival.AtMs - match.AtMs;
            double creation = nowMs - arrival.AtMs;
            interval.NetworkCount++;
            interval.NetworkSumMs += network;
            interval.NetworkMaxMs = Math.Max(interval.NetworkMaxMs, network);
            interval.CreationCount++;
            interval.CreationSumMs += creation;
            interval.CreationMaxMs = Math.Max(interval.CreationMaxMs, creation);
            if (perceived > SlowMs) interval.PerceivedOverOneSecond++;
            Witnessed(new Witness
            {
                DropPrefab = dropPrefab, SourcePrefab = match.Source, SourceKind = match.Kind,
                DistanceMetres = Math.Sqrt(match.DistanceSquared), NetworkMs = network, CreationMs = creation,
                PerceivedMs = perceived, CandidatesBefore = live, CandidatesAfterTable = table, OwnedExcluded = arrival.Owned
            });
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
            // The slowest four always, and every match over a second while room remains.
            int exported = (int)Math.Min(witnessCount, Math.Max(WitnessSlowest, Math.Min(WitnessCapacity, interval.PerceivedOverOneSecond)));
            summary.WitnessOverflow = Math.Max(0, interval.PerceivedOverOneSecond - WitnessCapacity);
            if (exported == 0) summary.Witnesses = Array.Empty<Witness>();
            else
            {
                var sorted = new Witness[witnessCount];
                Array.Copy(witnesses, sorted, witnessCount);
                Array.Sort(sorted, Slowest);
                summary.Witnesses = new Witness[exported];
                Array.Copy(sorted, summary.Witnesses, exported);
            }
            interval = default;
            witnessCount = 0;
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
                witnessCount = 0;
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

        // The owner's drops appear in the frame of its own destruction, at the kind's spawn
        // spread. Destructions recorded after their drops (tree, destructible) never match.
        private bool MatchOwner(double x, double y, double z, double nowMs, int dropPrefab)
        {
            int matches = 0;
            double destroyedAt = 0;
            for (int i = 0; i < destructions.Length; i++)
            {
                var entry = destructions[i];
                if (!entry.Active || !entry.Owned || !Live(entry, nowMs)) continue;
                if (DistanceSquared(entry.X, entry.Y, entry.Z, x, y, z) > entry.Radius * entry.Radius) continue;
                if (canSpawn != null && !canSpawn(entry.Source, dropPrefab)) continue;
                matches++;
                destroyedAt = entry.AtMs;
            }
            if (matches == 0) { interval.OwnerUnmatched++; return false; }
            if (matches > 1) { interval.OwnerAmbiguous++; return false; }
            double elapsed = nowMs - destroyedAt;
            interval.OwnerInstantiateCount++;
            interval.OwnerInstantiateSumMs += elapsed;
            interval.OwnerInstantiateMaxMs = Math.Max(interval.OwnerInstantiateMaxMs, elapsed);
            return true;
        }

        // Keeps the nearest MaxCandidates; one more marks the arrival truncated.
        private static void Keep(ref Arrival arrival, Candidate candidate)
        {
            if (arrival.Count < MaxCandidates) { Set(ref arrival, arrival.Count++, candidate); return; }
            arrival.Truncated = true;
            int farthest = 0;
            for (int i = 1; i < MaxCandidates; i++)
                if (Get(arrival, i).DistanceSquared > Get(arrival, farthest).DistanceSquared) farthest = i;
            if (candidate.DistanceSquared < Get(arrival, farthest).DistanceSquared) Set(ref arrival, farthest, candidate);
        }

        private void Witnessed(Witness witness)
        {
            if (witnessCount < WitnessCapacity) { witnesses[witnessCount++] = witness; return; }
            int fastest = 0;
            for (int i = 1; i < WitnessCapacity; i++)
                if (witnesses[i].PerceivedMs < witnesses[fastest].PerceivedMs) fastest = i;
            if (witness.PerceivedMs > witnesses[fastest].PerceivedMs) witnesses[fastest] = witness;
        }

        private static Candidate Get(in Arrival arrival, int index) => index switch
        {
            0 => arrival.C0, 1 => arrival.C1, 2 => arrival.C2, _ => arrival.C3
        };

        private static void Set(ref Arrival arrival, int index, Candidate candidate)
        {
            switch (index)
            {
                case 0: arrival.C0 = candidate; break;
                case 1: arrival.C1 = candidate; break;
                case 2: arrival.C2 = candidate; break;
                default: arrival.C3 = candidate; break;
            }
        }

        private int Nearest(double x, double y, double z, double nowMs)
        {
            int best = -1;
            double bestDistance = radiusSquared;
            for (int i = 0; i < destructions.Length; i++)
            {
                var entry = destructions[i];
                if (!entry.Active || !Live(entry, nowMs)) continue;
                double distance = DistanceSquared(entry.X, entry.Y, entry.Z, x, y, z);
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = i;
            }
            return best;
        }

        private static double DistanceSquared(double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return dx * dx + dy * dy + dz * dz;
        }

        private bool Live(Destruction entry, double nowMs) => nowMs >= entry.AtMs && nowMs - entry.AtMs < windowMs;

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
