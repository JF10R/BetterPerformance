using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    public static class CreationScheduling
    {
        // Never reduce vanilla's larger loading/backlog allowances.
        public static int ExpandQuota(int vanilla, int configured, bool enabled) =>
            enabled ? Math.Max(vanilla, configured) : vanilla;

        // Give vanilla ordering every fourth eligible pass. This limits priority
        // monopolization; it cannot guarantee latency under unbounded arrivals.
        public static bool TakePriorityTurn(ref int consecutivePriorityTurns)
        {
            if (consecutivePriorityTurns >= 3) { consecutivePriorityTurns = 0; return false; }
            consecutivePriorityTurns++;
            return true;
        }

        // Stable partition inside contiguous vanilla tiers. Never move an item
        // across a terrain/solid/other tier boundary. Scratch belongs to the caller.
        public static int PrioritizeWithinTiers<T>(List<T> candidates, List<T> scratch,
            Func<T, int> tier, Predicate<T> priority)
        {
            if (ReferenceEquals(candidates, scratch)) throw new ArgumentException("Scratch must be separate from candidates.", nameof(scratch));
            int prioritized = 0;
            scratch.Clear();
            try
            {
                for (int start = 0; start < candidates.Count;)
                {
                    int end = start + 1, currentTier = tier(candidates[start]);
                    while (end < candidates.Count && tier(candidates[end]) == currentTier) end++;
                    for (int i = start; i < end; i++)
                        if (priority(candidates[i])) { scratch.Add(candidates[i]); prioritized++; }
                    for (int i = start; i < end; i++)
                        if (!priority(candidates[i])) scratch.Add(candidates[i]);
                    start = end;
                }
                // Mutate only after classification succeeds for the complete list.
                for (int i = 0; i < candidates.Count; i++) candidates[i] = scratch[i];
                return prioritized;
            }
            finally { scratch.Clear(); }
        }
    }
}
