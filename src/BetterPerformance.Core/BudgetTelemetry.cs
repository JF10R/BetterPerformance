using System;

namespace BetterPerformance.Core
{
    // Single-threaded interval aggregates. A tracked candidate was next when the
    // budget gate yielded, but was not proven ready or cheaper without the budget.
    public sealed class BudgetTelemetry<TKey> where TKey : notnull
    {
        public struct Summary
        {
            public long NearYields, DistantYields, Batches, YieldedBatches, Attempts, Successes;
            public long CreationCalls, CreationFailures, CreationNullResults, CreationOvershoots, BatchOvershoots;
            public double CreationSumMs, CreationMaxMs, CreationExcessSumMs, BatchSumMs, BatchMaxMs;
            public LootQueueTracker<TKey>.Summary Waits;
        }

        private readonly LootQueueTracker<TKey> pending;
        private Summary interval;
        public int Pending => pending.Count;
        public bool Contains(TKey candidate) => pending.Contains(candidate);

        public BudgetTelemetry(int capacity = 512, double staleAfterMs = 30000, double maximumResidenceMs = 120000)
        {
            pending = new LootQueueTracker<TKey>(capacity, staleAfterMs, maximumResidenceMs);
        }

        public void RecordYield(TKey candidate, bool distant, double nowMs)
        {
            if (distant) interval.DistantYields++; else interval.NearYields++;
            pending.Expire(nowMs, 8);
            pending.Observe(candidate, nowMs);
        }

        public void RecordCreation(TKey candidate, double elapsedMs, double budgetMs, bool success, bool failed, double nowMs)
        {
            ValidateDuration(elapsedMs, nameof(elapsedMs));
            ValidateDuration(budgetMs, nameof(budgetMs));
            interval.CreationCalls++;
            if (failed) interval.CreationFailures++;
            else if (!success) interval.CreationNullResults++;
            interval.CreationSumMs += elapsedMs;
            interval.CreationMaxMs = Math.Max(interval.CreationMaxMs, elapsedMs);
            if (elapsedMs > budgetMs)
            {
                interval.CreationOvershoots++;
                interval.CreationExcessSumMs += elapsedMs - budgetMs;
            }
            if (success) pending.Complete(candidate, nowMs);
        }

        // A previously yielded candidate can complete after the budget is disabled.
        // Keep the observation, but do not count that call as budget-active work.
        public void CompletePending(TKey candidate, double nowMs) => pending.Complete(candidate, nowMs);

        public void RecordBatch(int attempts, int successes, bool yielded, double elapsedMs, double budgetMs)
        {
            ValidateDuration(elapsedMs, nameof(elapsedMs));
            ValidateDuration(budgetMs, nameof(budgetMs));
            interval.Batches++;
            if (yielded) interval.YieldedBatches++;
            interval.Attempts += attempts;
            interval.Successes += successes;
            interval.BatchSumMs += elapsedMs;
            interval.BatchMaxMs = Math.Max(interval.BatchMaxMs, elapsedMs);
            if (elapsedMs > budgetMs) interval.BatchOvershoots++;
        }

        public Summary Drain(double nowMs)
        {
            var summary = interval;
            summary.Waits = pending.Drain(nowMs);
            interval = default;
            return summary;
        }

        public void Clear(bool censorPending)
        {
            pending.Clear(censorPending);
            if (!censorPending) interval = default;
        }

        private static void ValidateDuration(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                throw new ArgumentOutOfRangeException(name);
        }
    }
}
