using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    // First observation is not first enqueue: every duration is a lower bound on
    // local queue residence, never network or player-action latency. Single-threaded.
    public sealed class LootQueueTracker<TKey> where TKey : notnull
    {
        private struct Entry
        {
            internal bool Active;
            internal TKey Key;
            internal double FirstSeen, LastSeen;
        }

        public struct Summary
        {
            public long Observed, Completed, Censored, CapacitySkipped;
            public double CompletedSumMs, CompletedMaxMs, PendingMaxAgeMs;
            public int Pending;
        }

        private readonly Entry[] entries;
        private readonly Dictionary<TKey, int> slots;
        private readonly Queue<int> free;
        private readonly double staleAfterMs, maximumResidenceMs;
        private int sweep;
        private Summary interval;

        public LootQueueTracker(int capacity, double staleAfterMs, double maximumResidenceMs)
        {
            if (capacity < 1 || capacity > 16384) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!(staleAfterMs > 0) || double.IsInfinity(staleAfterMs)) throw new ArgumentOutOfRangeException(nameof(staleAfterMs));
            if (!(maximumResidenceMs >= staleAfterMs) || double.IsInfinity(maximumResidenceMs)) throw new ArgumentOutOfRangeException(nameof(maximumResidenceMs));
            entries = new Entry[capacity];
            slots = new Dictionary<TKey, int>(capacity);
            free = new Queue<int>(capacity);
            for (int i = 0; i < capacity; i++) free.Enqueue(i);
            this.staleAfterMs = staleAfterMs;
            this.maximumResidenceMs = maximumResidenceMs;
        }

        public int Count => slots.Count;

        // Membership only: expiration and duration accounting remain in Complete.
        public bool Contains(TKey key) => slots.ContainsKey(key);

        public bool Observe(TKey key, double nowMs)
        {
            ValidateTime(nowMs);
            if (slots.TryGetValue(key, out int slot))
            {
                if (!Expired(entries[slot], nowMs))
                {
                    entries[slot].LastSeen = nowMs;
                    return true;
                }
                Remove(slot, true);
            }
            if (free.Count == 0) { interval.CapacitySkipped++; return false; }
            slot = free.Dequeue();
            entries[slot] = new Entry { Active = true, Key = key, FirstSeen = nowMs, LastSeen = nowMs };
            slots.Add(key, slot);
            interval.Observed++;
            return true;
        }

        public bool Complete(TKey key, double nowMs)
        {
            ValidateTime(nowMs);
            if (!slots.TryGetValue(key, out int slot)) return false;
            if (Expired(entries[slot], nowMs)) { Remove(slot, true); return false; }
            double elapsed = Math.Max(0, nowMs - entries[slot].FirstSeen);
            interval.Completed++;
            interval.CompletedSumMs += elapsed;
            interval.CompletedMaxMs = Math.Max(interval.CompletedMaxMs, elapsed);
            Remove(slot, false);
            return true;
        }

        public void Expire(double nowMs, int checks)
        {
            ValidateTime(nowMs);
            if (checks < 0) throw new ArgumentOutOfRangeException(nameof(checks));
            for (int i = 0; i < Math.Min(checks, entries.Length); i++)
            {
                int slot = sweep;
                sweep = (sweep + 1) % entries.Length;
                if (entries[slot].Active && Expired(entries[slot], nowMs)) Remove(slot, true);
            }
        }

        public Summary Drain(double nowMs)
        {
            Expire(nowMs, entries.Length);
            var summary = interval;
            summary.Pending = Count;
            foreach (var entry in entries)
                if (entry.Active) summary.PendingMaxAgeMs = Math.Max(summary.PendingMaxAgeMs, Math.Max(0, nowMs - entry.FirstSeen));
            interval = default;
            return summary;
        }

        // A scene change censors pending observations. A new capture resets all
        // counters instead, so records from independent captures cannot mix.
        public void Clear(bool censorPending)
        {
            if (censorPending) interval.Censored += slots.Count;
            else interval = default;
            Array.Clear(entries, 0, entries.Length);
            slots.Clear();
            free.Clear();
            for (int i = 0; i < entries.Length; i++) free.Enqueue(i);
            sweep = 0;
        }

        private bool Expired(Entry entry, double nowMs) => nowMs < entry.LastSeen ||
            nowMs - entry.LastSeen >= staleAfterMs || nowMs - entry.FirstSeen >= maximumResidenceMs;

        private void Remove(int slot, bool censored)
        {
            slots.Remove(entries[slot].Key);
            entries[slot] = default;
            free.Enqueue(slot);
            if (censored) interval.Censored++;
        }

        private static void ValidateTime(double nowMs)
        {
            if (double.IsNaN(nowMs) || double.IsInfinity(nowMs)) throw new ArgumentOutOfRangeException(nameof(nowMs));
        }
    }
}
