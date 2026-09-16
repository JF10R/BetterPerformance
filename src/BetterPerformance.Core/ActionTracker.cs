using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    public enum ObservedAction { Pickup, Container }

    // Main-thread observer only. Keys never leave this bounded tracker or appear in output.
    public sealed class ActionTracker<TKey> where TKey : class
    {
        public struct Summary
        {
            public long Started, RequestCalls, DirectCalls, Confirmed, Rejected, Censored, TimedOut;
            public long Duplicates, Unmatched, CapacitySkipped, OwnershipRequests, AmbiguousConfirmed;
            public long RequestCompleted, DirectCompleted, OwnershipCompleted;
            public double RequestSumMs, RequestMaxMs, DirectSumMs, DirectMaxMs, OwnershipSumMs, OwnershipMaxMs;
            public int Pending;
        }
        private struct Entry
        {
            internal ObservedAction Kind;
            internal double Start, DirectStart;
            internal bool HasRequest, HasDirect, Ownership, RequestAmbiguous, DirectAmbiguous;
        }
        private readonly Dictionary<TKey, Entry> pending;
        private readonly TKey?[] expired;
        private readonly Summary[] summaries = new Summary[2];
        private readonly double timeoutSeconds;

        public ActionTracker(int capacity = 128, double timeoutSeconds = 30, IEqualityComparer<TKey>? comparer = null)
        {
            if (capacity < 1 || capacity > 128 || timeoutSeconds <= 0 || double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds))
                throw new ArgumentOutOfRangeException();
            pending = new Dictionary<TKey, Entry>(capacity, comparer);
            expired = new TKey?[capacity];
            this.timeoutSeconds = timeoutSeconds;
        }

        public void BeginRequest(TKey key, ObservedAction kind, double now) => Begin(key, kind, now, false);
        public void BeginDirect(TKey key, ObservedAction kind, double now) => Begin(key, kind, now, true);
        private void Begin(TKey key, ObservedAction kind, double now, bool direct)
        {
            Validate(kind, now);
            ref var summary = ref summaries[(int)kind];
            if (direct) summary.DirectCalls++; else summary.RequestCalls++;
            if (pending.TryGetValue(key, out var entry))
            {
                if (now - entry.Start >= timeoutSeconds) RemoveCensored(key, entry, true);
                else
                {
                    if (entry.Kind != kind) throw new InvalidOperationException("Action kind changed for a live key.");
                    if (direct && !entry.HasDirect)
                    {
                        entry.HasDirect = true; entry.DirectStart = now; pending[key] = entry;
                    }
                    else
                    {
                        summary.Duplicates++;
                        if (direct) entry.DirectAmbiguous = true; else entry.RequestAmbiguous = true;
                        pending[key] = entry;
                    }
                    return;
                }
            }
            if (pending.Count == expired.Length) { summary.CapacitySkipped++; return; }
            pending.Add(key, new Entry { Kind = kind, Start = now, DirectStart = now, HasRequest = !direct, HasDirect = direct });
            summary.Started++;
        }

        public void MarkOwnershipRequest(TKey key)
        {
            if (!pending.TryGetValue(key, out var entry) || !entry.HasRequest || entry.Ownership) return;
            entry.Ownership = true; pending[key] = entry;
            summaries[(int)entry.Kind].OwnershipRequests++;
        }

        public void Complete(TKey key, ObservedAction kind, double now, bool accepted)
        {
            Validate(kind, now);
            if (!pending.TryGetValue(key, out var entry) || entry.Kind != kind)
            { summaries[(int)kind].Unmatched++; return; }
            if (now - entry.Start >= timeoutSeconds)
            {
                RemoveCensored(key, entry, true);
                summaries[(int)kind].Unmatched++;
                return;
            }
            pending.Remove(key);
            ref var summary = ref summaries[(int)kind];
            if (!accepted) { summary.Rejected++; return; }
            summary.Confirmed++;
            if (entry.RequestAmbiguous || entry.DirectAmbiguous) summary.AmbiguousConfirmed++;
            if (entry.HasRequest && !entry.RequestAmbiguous)
            {
                double delay = Math.Max(0, now - entry.Start) * 1000;
                summary.RequestCompleted++; summary.RequestSumMs += delay; summary.RequestMaxMs = Math.Max(summary.RequestMaxMs, delay);
                if (entry.Ownership)
                {
                    summary.OwnershipCompleted++; summary.OwnershipSumMs += delay;
                    summary.OwnershipMaxMs = Math.Max(summary.OwnershipMaxMs, delay);
                }
            }
            if (entry.HasDirect && !entry.DirectAmbiguous)
            {
                double delay = Math.Max(0, now - entry.DirectStart) * 1000;
                summary.DirectCompleted++; summary.DirectSumMs += delay; summary.DirectMaxMs = Math.Max(summary.DirectMaxMs, delay);
            }
        }

        public void Censor(TKey key)
        {
            if (pending.TryGetValue(key, out var entry)) RemoveCensored(key, entry, false);
        }
        public void Expire(double now)
        {
            Validate(ObservedAction.Pickup, now);
            int count = 0;
            foreach (var pair in pending) if (now - pair.Value.Start >= timeoutSeconds) expired[count++] = pair.Key;
            for (int index = 0; index < count; index++)
            {
                var key = expired[index]!; RemoveCensored(key, pending[key], true); expired[index] = null;
            }
        }
        public void CensorAll()
        {
            foreach (var pair in pending) summaries[(int)pair.Value.Kind].Censored++;
            pending.Clear();
        }
        public Summary Drain(ObservedAction kind)
        {
            Validate(kind, 0);
            var summary = summaries[(int)kind]; summaries[(int)kind] = default;
            foreach (var pair in pending) if (pair.Value.Kind == kind) summary.Pending++;
            return summary;
        }
        public void Reset() { pending.Clear(); Array.Clear(summaries, 0, summaries.Length); Array.Clear(expired, 0, expired.Length); }
        private void RemoveCensored(TKey key, Entry entry, bool timeout)
        {
            pending.Remove(key);
            ref var summary = ref summaries[(int)entry.Kind]; summary.Censored++;
            if (timeout) summary.TimedOut++;
        }
        private static void Validate(ObservedAction kind, double now)
        {
            if ((int)kind < 0 || (int)kind > 1 || now < 0 || double.IsInfinity(now) || double.IsNaN(now))
                throw new ArgumentOutOfRangeException();
        }
    }
}
