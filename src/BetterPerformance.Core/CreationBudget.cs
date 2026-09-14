namespace BetterPerformance.Core
{
    // Monotonic clock units supplied by the caller. One success guarantees progress
    // even when candidate scanning, sorting or readiness checks consume the budget.
    public struct CreationBudget
    {
        private readonly long started, allowance;
        public int Attempts { get; private set; }
        public int Successes { get; private set; }

        public CreationBudget(long started, long allowance)
        {
            this.started = started;
            this.allowance = allowance;
            Attempts = 0;
            Successes = 0;
        }

        public void RecordCreation(bool success) { Attempts++; if (success) Successes++; }
        public bool AllowNext(bool hasNext, long now) =>
            hasNext && (Successes == 0 || now - started < allowance);
    }
}
