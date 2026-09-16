using System;

namespace BetterPerformance.Core
{
    public enum ThreadCpuWindowStatus { Warmup, Available, InvalidDelta }

    // Cumulative native CPU counters and a monotonic wall clock must share endpoints.
    public sealed class ThreadCpuWindow
    {
        private readonly long frequency;
        private bool hasBaseline;
        private long previousTimestamp;
        private ulong previousKernel, previousUser;

        public ThreadCpuWindow(long timestampFrequency)
        {
            if (timestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
            frequency = timestampFrequency;
        }

        public void Reset() { hasBaseline = false; }

        public ThreadCpuWindowStatus Observe(long timestamp, ulong kernel100ns, ulong user100ns,
            out double cpuMs, out double wallMs)
        {
            cpuMs = wallMs = 0;
            ThreadCpuWindowStatus status = ThreadCpuWindowStatus.Warmup;
            if (hasBaseline)
            {
                status = ThreadCpuWindowStatus.InvalidDelta;
                if (timestamp > previousTimestamp && kernel100ns >= previousKernel && user100ns >= previousUser)
                {
                    // Convert before summing: cumulative counters must never overflow an integer sum.
                    cpuMs = ((double)(kernel100ns - previousKernel) + (user100ns - previousUser)) / 10000.0;
                    wallMs = ((double)timestamp - previousTimestamp) * 1000.0 / frequency;
                    if (wallMs > 0 && !double.IsInfinity(wallMs)) status = ThreadCpuWindowStatus.Available;
                    else cpuMs = wallMs = 0;
                }
            }
            // A regression primes a fresh window; no later sample spans the bad observation.
            previousTimestamp = timestamp;
            previousKernel = kernel100ns;
            previousUser = user100ns;
            hasBaseline = true;
            return status;
        }
    }
}
