using System;

namespace BetterPerformance.Core
{
    public struct FixedCatchupSnapshot
    {
        public long PairedFrames, PairedSteps, CatchupFrames, ExtraSteps, StepsBeyondCap;
        public double FixedMsSum, FixedMsMax, CatchupFixedMsSum, ProjectedSavedMs;
    }

    // Cost of the fixed steps Unity replays after a slow frame (up to maximumDeltaTime /
    // fixedDeltaTime per frame), and what a lower cap would have skipped. A frame with more
    // than two steps is a catch-up frame. Read-only: no engine setting is changed.
    public sealed class FixedCatchupWindow
    {
        public const long CatchupSteps = 3;
        private FixedCatchupSnapshot current;

        // One completed frame: its fixed-step count and the time its fixed phase took.
        public void Note(long steps, double fixedMs, long capSteps)
        {
            if (steps <= 0 || double.IsNaN(fixedMs) || double.IsInfinity(fixedMs) || fixedMs < 0) return;
            current.PairedFrames++;
            current.PairedSteps += steps;
            current.FixedMsSum += fixedMs;
            if (fixedMs > current.FixedMsMax) current.FixedMsMax = fixedMs;
            if (steps >= CatchupSteps)
            {
                current.CatchupFrames++;
                current.ExtraSteps += steps - 2;
                current.CatchupFixedMsSum += fixedMs;
            }
            if (capSteps > 0 && steps > capSteps)
            {
                current.StepsBeyondCap += steps - capSteps;
                current.ProjectedSavedMs += fixedMs * (steps - capSteps) / steps;
            }
        }

        public FixedCatchupSnapshot Drain()
        {
            var snapshot = current;
            current = default;
            return snapshot;
        }

        // Steps a maximumDeltaTime cap allows in one frame, as Unity computes it.
        public static long CapSteps(double capSeconds, double fixedDeltaSeconds) =>
            fixedDeltaSeconds > 0 && capSeconds > 0 ? (long)Math.Floor(capSeconds / fixedDeltaSeconds + 1e-9) : 0;
    }

    public struct FrameBusySnapshot
    {
        public long Frames, Over60Hz, Over120Hz, Over144Hz;
        public double BusyMsSum, BusyMsMax, WaitMsSum;
    }

    // Per-frame work excluding the frame-rate wait (frame time minus the pacing wait), against the
    // budgets of 60, 120 and 144 ticks per second: what a faster server tick would have to fit.
    public sealed class FrameBusyWindow
    {
        public const double Budget60 = 1000.0 / 60, Budget120 = 1000.0 / 120, Budget144 = 1000.0 / 144;
        private FrameBusySnapshot current;

        public void Note(double loopMs, double waitMs)
        {
            if (double.IsNaN(loopMs) || loopMs < 0 || double.IsInfinity(loopMs)) return;
            if (double.IsNaN(waitMs) || waitMs < 0 || double.IsInfinity(waitMs)) waitMs = 0;
            double busy = Math.Max(0, loopMs - waitMs);
            current.Frames++;
            current.BusyMsSum += busy;
            current.WaitMsSum += Math.Min(waitMs, loopMs);
            if (busy > current.BusyMsMax) current.BusyMsMax = busy;
            if (busy > Budget60) current.Over60Hz++;
            if (busy > Budget120) current.Over120Hz++;
            if (busy > Budget144) current.Over144Hz++;
        }

        public FrameBusySnapshot Drain()
        {
            var snapshot = current;
            current = default;
            return snapshot;
        }
    }
}
