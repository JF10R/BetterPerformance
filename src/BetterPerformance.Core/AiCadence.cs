using System;

namespace BetterPerformance.Core
{
    public struct AiCadenceSnapshot
    {
        public long Batches, RepeatedFrameBatches, GapCount, InputCountSum, Rejected;
        public int InputCountLast, InputCountMax, ScratchCountLast, ScratchCountMax;
        public double GapSumMs, GapMaxMs, DeltaSumMs, DeltaMaxMs;
    }

    // Main-thread observations only. No per-AI state, histories or retained objects.
    public sealed class AiCadence
    {
        private bool hasPrevious;
        private double previousSeconds;
        private int previousFrame;
        private AiCadenceSnapshot interval;

        public bool Observe(double seconds, int frame, int inputCount, int scratchCount, double deltaSeconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 ||
                double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds < 0 ||
                inputCount < 0 || scratchCount < 0 || (hasPrevious && seconds < previousSeconds))
            { interval.Rejected++; return false; }
            if (hasPrevious)
            {
                double gap = (seconds - previousSeconds) * 1000;
                interval.GapCount++;
                interval.GapSumMs += gap;
                interval.GapMaxMs = Math.Max(interval.GapMaxMs, gap);
                if (frame == previousFrame) interval.RepeatedFrameBatches++;
            }
            interval.Batches++;
            interval.InputCountSum += inputCount;
            interval.InputCountLast = inputCount;
            interval.InputCountMax = Math.Max(interval.InputCountMax, inputCount);
            interval.ScratchCountLast = scratchCount;
            interval.ScratchCountMax = Math.Max(interval.ScratchCountMax, scratchCount);
            interval.DeltaSumMs += deltaSeconds * 1000;
            interval.DeltaMaxMs = Math.Max(interval.DeltaMaxMs, deltaSeconds * 1000);
            previousSeconds = seconds;
            previousFrame = frame;
            hasPrevious = true;
            return true;
        }

        public AiCadenceSnapshot Drain()
        {
            AiCadenceSnapshot snapshot = interval;
            interval = default;
            return snapshot;
        }

        public void Reset()
        {
            interval = default;
            hasPrevious = false;
            previousSeconds = 0;
            previousFrame = 0;
        }
    }
}
