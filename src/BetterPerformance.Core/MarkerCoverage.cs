using System;

namespace BetterPerformance.Core
{
    // Share of an interval's frames whose engine marker sample survived the recorder ring.
    public static class MarkerCoverage
    {
        // Frames each timing recorder keeps between drains: a 10 s poll (the collector's backoff ceiling) at 400 fps.
        public const int RingCapacity = 4096;

        // Drained samples over frames seen, in percent, capped at 100; NaN when either side is unmeasured.
        public static double Percent(long drained, long framesSeen)
        {
            if (drained < 0 || framesSeen <= 0) return double.NaN;
            return Math.Min(100.0, drained * 100.0 / framesSeen);
        }
    }
}
