namespace BetterPerformance.Core
{
    public enum SystemCpuWindowStatus { Warmup, Available, InvalidDelta }

    // Whole-machine CPU busy share from cumulative GetSystemTimes counters (100 ns units).
    // Kernel time INCLUDES idle time, so busy = kernel + user - idle over kernel + user.
    public sealed class SystemCpuWindow
    {
        private bool hasBaseline;
        private ulong previousIdle, previousKernel, previousUser;

        public void Reset() { hasBaseline = false; }

        public SystemCpuWindowStatus Observe(ulong idle100ns, ulong kernel100ns, ulong user100ns, out double busyPercent)
        {
            busyPercent = 0;
            SystemCpuWindowStatus status = SystemCpuWindowStatus.Warmup;
            if (hasBaseline)
            {
                status = SystemCpuWindowStatus.InvalidDelta;
                if (idle100ns >= previousIdle && kernel100ns >= previousKernel && user100ns >= previousUser)
                {
                    // Convert before summing: cumulative counters must never overflow an integer sum.
                    double idle = idle100ns - previousIdle;
                    double total = (double)(kernel100ns - previousKernel) + (user100ns - previousUser);
                    if (total > 0 && idle <= total)
                    {
                        busyPercent = (total - idle) / total * 100.0;
                        status = SystemCpuWindowStatus.Available;
                    }
                }
            }
            // A regression primes a fresh window; no later sample spans the bad observation.
            previousIdle = idle100ns;
            previousKernel = kernel100ns;
            previousUser = user100ns;
            hasBaseline = true;
            return status;
        }
    }
}
