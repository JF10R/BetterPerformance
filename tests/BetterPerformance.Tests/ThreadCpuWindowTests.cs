using System;
using BetterPerformance.Core;

internal static class ThreadCpuWindowTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var window = new ThreadCpuWindow(1000);
        double cpuMs, wallMs;
        Check(window.Observe(1000, 100000, 200000, out cpuMs, out wallMs) == ThreadCpuWindowStatus.Warmup,
            "First observation must not fabricate idle CPU time.");
        Check(window.Observe(2000, 1100000, 4200000, out cpuMs, out wallMs) == ThreadCpuWindowStatus.Available
            && cpuMs == 500 && wallMs == 1000, "Combine kernel and user increments over their own wall window.");
        Check(window.Observe(3000, 1100000, 4200000, out cpuMs, out wallMs) == ThreadCpuWindowStatus.Available
            && cpuMs == 0 && wallMs == 1000, "A valid idle interval is distinguishable from unavailable data.");
        Check(window.Observe(3000, 1100000, 4200000, out cpuMs, out wallMs) == ThreadCpuWindowStatus.InvalidDelta,
            "Duplicate wall endpoints cannot produce a CPU percentage.");
        Check(window.Observe(4000, 0, 0, out cpuMs, out wallMs) == ThreadCpuWindowStatus.InvalidDelta,
            "Regressing CPU counters must not wrap into huge activity.");
        Check(window.Observe(5000, 10000, 0, out cpuMs, out wallMs) == ThreadCpuWindowStatus.Available
            && cpuMs == 1 && wallMs == 1000, "After an invalid sample, recover using a fresh baseline.");
        window.Reset();
        Check(window.Observe(0, ulong.MaxValue - 10000, ulong.MaxValue - 10000, out cpuMs, out wallMs)
            == ThreadCpuWindowStatus.Warmup, "Reset drops the previous capture epoch.");
        Check(window.Observe(1000, ulong.MaxValue, ulong.MaxValue, out cpuMs, out wallMs)
            == ThreadCpuWindowStatus.Available && cpuMs == 2, "Cumulative kernel/user sums cannot overflow.");
        window.Reset(); // Same operation used after a failed native sample.
        Check(window.Observe(9000, 200000, 100000, out cpuMs, out wallMs) == ThreadCpuWindowStatus.Warmup,
            "A read failure invalidates both CPU and wall endpoints.");
    }
}
