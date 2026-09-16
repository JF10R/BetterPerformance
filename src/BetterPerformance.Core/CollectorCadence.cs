using System;

namespace BetterPerformance.Core
{
    // Main-thread polling only. Method timing hooks remain active during backoff.
    public sealed class CollectorCadence
    {
        public const double SoftBudgetMs = 2;
        private readonly double minimumSeconds;
        private int cheapPolls;
        public double IntervalSeconds { get; private set; }
        public double LastCostMs { get; private set; }
        public long Overruns { get; private set; }

        public CollectorCadence(double minimumSeconds)
        {
            if (double.IsNaN(minimumSeconds) || minimumSeconds < 0.5 || minimumSeconds > 10)
                throw new ArgumentOutOfRangeException(nameof(minimumSeconds));
            this.minimumSeconds = IntervalSeconds = minimumSeconds;
        }

        public void Observe(double costMs)
        {
            if (double.IsNaN(costMs) || double.IsInfinity(costMs) || costMs < 0)
                throw new ArgumentOutOfRangeException(nameof(costMs));
            LastCostMs = costMs;
            if (costMs > SoftBudgetMs)
            {
                Overruns++;
                cheapPolls = 0;
                IntervalSeconds = Math.Min(10, IntervalSeconds * 2);
            }
            else if (++cheapPolls >= 30)
            {
                cheapPolls = 0;
                IntervalSeconds = Math.Max(minimumSeconds, IntervalSeconds / 2);
            }
        }
    }
}
