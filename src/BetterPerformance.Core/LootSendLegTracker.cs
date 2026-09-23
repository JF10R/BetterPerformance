using System;

namespace BetterPerformance.Core
{
    // First-send leg of a new drop: from when this process starts following it (its own
    // Instantiate, or first receipt on the server) to the first ZDOData send that carries
    // it to each peer. Bounded to `capacity` drops and PeerSlots peers each; the caller
    // reports sends through ISentSet. Single-threaded.
    public sealed class LootSendLegTracker<TKey> where TKey : IEquatable<TKey>
    {
        public const int PeerSlots = 8;
        public const double SlowMs = 1000;

        public interface ISentSet { bool Contains(TKey key); }

        private struct Pending
        {
            internal TKey Key;
            internal double StartMs;
            internal long Excluded;
            internal int Done;
        }

        public struct Summary
        {
            public long Started, Skipped, Count, OverOneSecond, ExpiredUnsent, PeerSlotsFull, NonMonotonic, Censored;
            public double SumMs, MaxMs;
            public int Pending;
        }

        private readonly Pending[] pending;
        private readonly long[] peers;
        private readonly bool firstPeerCompletes;
        private readonly double windowMs;
        private int count;
        private Summary interval;

        // firstPeerCompletes: a client has one peer, the server; a server follows every
        // other peer until the window ends.
        public LootSendLegTracker(int capacity, double windowMs, bool firstPeerCompletes)
        {
            if (capacity < 1 || capacity > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!(windowMs > 0) || double.IsInfinity(windowMs)) throw new ArgumentOutOfRangeException(nameof(windowMs));
            pending = new Pending[capacity];
            peers = new long[capacity * PeerSlots];
            this.windowMs = windowMs;
            this.firstPeerCompletes = firstPeerCompletes;
        }

        public int PendingCount => count;

        // excludedPeer is the peer the drop came from; a send back to it is not a delivery.
        public bool Start(TKey key, long excludedPeer, double nowMs)
        {
            ValidateTime(nowMs);
            for (int i = 0; i < count; i++)
                if (pending[i].Key.Equals(key)) return true;
            if (count >= pending.Length) { interval.Skipped++; return false; }
            pending[count++] = new Pending { Key = key, StartMs = nowMs, Excluded = excludedPeer };
            interval.Started++;
            return true;
        }

        // Called after one send to `peer`: each followed drop that send carried completes
        // for that peer exactly once. Returns the number of samples taken.
        public int Sent(long peer, ISentSet sent, double nowMs)
        {
            ValidateTime(nowMs);
            if (sent == null) throw new ArgumentNullException(nameof(sent));
            int completed = 0;
            for (int i = 0; i < count;)
            {
                if (peer == pending[i].Excluded || Reached(i, peer) || !sent.Contains(pending[i].Key)) { i++; continue; }
                double elapsed = nowMs - pending[i].StartMs;
                if (elapsed < 0) interval.NonMonotonic++;
                else
                {
                    completed++;
                    interval.Count++;
                    interval.SumMs += elapsed;
                    interval.MaxMs = Math.Max(interval.MaxMs, elapsed);
                    if (elapsed > SlowMs) interval.OverOneSecond++;
                }
                if (firstPeerCompletes) { RemoveAt(i); continue; }
                peers[i * PeerSlots + pending[i].Done++] = peer;
                // No slot left to remember the next peer, so stop rather than resample.
                if (pending[i].Done == PeerSlots) { interval.PeerSlotsFull++; RemoveAt(i); continue; }
                i++;
            }
            return completed;
        }

        public void Expire(double nowMs)
        {
            ValidateTime(nowMs);
            for (int i = 0; i < count;)
            {
                double age = nowMs - pending[i].StartMs;
                if (age >= 0 && age < windowMs) { i++; continue; }
                if (pending[i].Done == 0) interval.ExpiredUnsent++;
                RemoveAt(i);
            }
        }

        public Summary Drain(double nowMs)
        {
            Expire(nowMs);
            var summary = interval;
            summary.Pending = count;
            interval = default;
            return summary;
        }

        // Same contract as the observer tracker: a scene change censors, a new capture resets.
        public void Clear(bool censorPending)
        {
            if (censorPending) interval.Censored += count;
            else interval = default;
            Array.Clear(pending, 0, pending.Length);
            count = 0;
        }

        private bool Reached(int index, long peer)
        {
            int start = index * PeerSlots;
            for (int i = 0; i < pending[index].Done; i++)
                if (peers[start + i] == peer) return true;
            return false;
        }

        private void RemoveAt(int index)
        {
            int last = --count;
            if (index != last)
            {
                pending[index] = pending[last];
                Array.Copy(peers, last * PeerSlots, peers, index * PeerSlots, PeerSlots);
            }
            pending[last] = default;
        }

        private static void ValidateTime(double nowMs)
        {
            if (double.IsNaN(nowMs) || double.IsInfinity(nowMs)) throw new ArgumentOutOfRangeException(nameof(nowMs));
        }
    }
}
