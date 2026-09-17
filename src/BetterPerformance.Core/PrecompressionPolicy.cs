using System;

namespace BetterPerformance.Core
{
    public enum PrecompressionDecision
    {
        // Nothing changed since the last dispatched run; a new run would produce the same entry.
        Unchanged,
        // The change counter is still moving; the quiet window has not elapsed.
        Dirty,
        // A worker is still encoding, or its result has not been adopted yet.
        InFlight,
        // The minimum interval between dispatches has not elapsed.
        TooSoon,
        // The per-minute cap is spent.
        RateLimited,
        Run,
    }

    // Decides when a speculative run may start. Pure and clock-injected so the trigger
    // policy and the in-flight guard are testable without a game or a worker.
    public sealed class PrecompressionPolicy
    {
        private readonly double quietSeconds, minimumIntervalSeconds;
        private readonly double[] dispatches;
        private long observedChange, dispatchedChange;
        private double lastChangeSeconds, lastDispatchSeconds;
        private bool started, inFlight, resultPending;
        private int dispatchCount;

        public PrecompressionPolicy(double quietSeconds, double minimumIntervalSeconds, int maximumRunsPerMinute)
        {
            if (quietSeconds < 0) throw new ArgumentOutOfRangeException(nameof(quietSeconds));
            if (minimumIntervalSeconds < 0) throw new ArgumentOutOfRangeException(nameof(minimumIntervalSeconds));
            if (maximumRunsPerMinute < 1) throw new ArgumentOutOfRangeException(nameof(maximumRunsPerMinute));
            this.quietSeconds = quietSeconds;
            this.minimumIntervalSeconds = minimumIntervalSeconds;
            dispatches = new double[maximumRunsPerMinute];
        }

        public bool InFlight => inFlight;
        public bool ResultPending => resultPending;

        // changeCounter is any monotonic-in-observation value that differs whenever the map
        // payload may differ. Equality, not ordering, is what the policy uses.
        public PrecompressionDecision Evaluate(double nowSeconds, long changeCounter)
        {
            if (!started || changeCounter != observedChange)
            {
                started = true;
                observedChange = changeCounter;
                lastChangeSeconds = nowSeconds;
            }
            if (inFlight || resultPending) return PrecompressionDecision.InFlight;
            if (dispatchCount > 0 && changeCounter == dispatchedChange) return PrecompressionDecision.Unchanged;
            if (nowSeconds - lastChangeSeconds < quietSeconds) return PrecompressionDecision.Dirty;
            if (dispatchCount > 0 && nowSeconds - lastDispatchSeconds < minimumIntervalSeconds) return PrecompressionDecision.TooSoon;
            if (RunsInLastMinute(nowSeconds) >= dispatches.Length) return PrecompressionDecision.RateLimited;
            return PrecompressionDecision.Run;
        }

        public void Dispatch(double nowSeconds, long changeCounter)
        {
            if (inFlight || resultPending) throw new InvalidOperationException("A speculative run is already in flight.");
            inFlight = true;
            dispatchedChange = changeCounter;
            lastDispatchSeconds = nowSeconds;
            dispatches[dispatchCount % dispatches.Length] = nowSeconds;
            dispatchCount++;
        }

        // A failed run leaves nothing to adopt; a successful one parks a result for the pump.
        public void Complete(bool produced)
        {
            inFlight = false;
            resultPending = produced;
        }

        public void Adopted() { resultPending = false; }

        // A new world or an uninstall must not carry the previous world's dispatch state.
        public void Reset()
        {
            observedChange = dispatchedChange = 0;
            lastChangeSeconds = lastDispatchSeconds = 0;
            started = inFlight = resultPending = false;
            dispatchCount = 0;
            Array.Clear(dispatches, 0, dispatches.Length);
        }

        private int RunsInLastMinute(double nowSeconds)
        {
            int count = 0;
            int considered = dispatchCount < dispatches.Length ? dispatchCount : dispatches.Length;
            for (int i = 0; i < considered; i++) if (nowSeconds - dispatches[i] < 60.0) count++;
            return count;
        }
    }
}
