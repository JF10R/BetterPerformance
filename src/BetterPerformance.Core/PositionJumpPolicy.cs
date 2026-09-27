using System;

namespace BetterPerformance.Core
{
    // When a reference position that jumped (portal, respawn) is sent at once instead of waiting
    // for the native 2 s cadence. The value sent is the one vanilla holds; only the delay changes.
    // docs/position-jump-sync.md.
    public static class PositionJumpPolicy
    {
        public const double DefaultJumpMeters = 64, MinIntervalSeconds = 0.25;

        public static bool IsJump(double distanceMeters, double thresholdMeters) =>
            !double.IsNaN(distanceMeters) && !double.IsInfinity(distanceMeters) && thresholdMeters > 0 && distanceMeters >= thresholdMeters;

        // At most one early send per MinIntervalSeconds, so a burst of jumps cannot flood the link.
        public static bool MaySend(double now, double lastEarlySend) =>
            double.IsNaN(lastEarlySend) || now - lastEarlySend >= MinIntervalSeconds || now < lastEarlySend;
    }
}
