using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    public enum Admission { Admitted, Capacity, Duplicate }

    // A per-window admission cap with duplicate suppression, on a caller-supplied millisecond clock.
    // Operational state only: statistics live with the caller, so resetting them never reopens a window.
    public sealed class AdmissionWindow<TKey> where TKey : IEquatable<TKey>
    {
        private readonly HashSet<TKey> keys = new HashSet<TKey>();
        private readonly long windowMilliseconds;
        private readonly int dedupeLimit;
        private long start, count;

        public AdmissionWindow(long windowMilliseconds, int dedupeLimit)
        {
            this.windowMilliseconds = windowMilliseconds;
            this.dedupeLimit = dedupeLimit;
        }

        public int Count => keys.Count;

        public Admission Admit(TKey key, long nowMilliseconds, int limit)
        {
            if (nowMilliseconds - start >= windowMilliseconds)
            {
                start = nowMilliseconds;
                count = 0;
                keys.Clear();
            }
            if (limit < 1 || count >= limit || keys.Count >= dedupeLimit) return Admission.Capacity;
            if (!keys.Add(key)) return Admission.Duplicate;
            count++;
            return Admission.Admitted;
        }

        // Uninstall only: a new install starts a fresh window at clock zero.
        public void Clear()
        {
            keys.Clear();
            start = count = 0;
        }
    }
}
