using System;

namespace BetterPerformance.Core
{
    public enum TeleportMilestone { Moved, ZoneLoaded, ActiveAreaLoaded, AreaReady, FloorFound }
    public enum TeleportOutcome { None, Arrived, Aborted }

    // One local-player teleport, from the accepted TeleportTo to the frame the native
    // UpdateTeleport clears m_teleporting. Times are seconds on one monotonic clock;
    // a milestone keeps the first time it was seen true. docs/teleport-loading.md.
    public sealed class TeleportTimeline
    {
        private readonly double[] times = new double[5];
        public bool Active { get; private set; }
        public bool Distant { get; private set; }
        public double DistanceMeters { get; private set; }
        public double Started { get; private set; } = double.NaN;
        public double Ended { get; private set; } = double.NaN;
        public TeleportOutcome Outcome { get; private set; }
        public long Frames { get; private set; }
        public double FrameMaxMs { get; private set; }
        public long ReplacedIncomplete { get; private set; }

        public TeleportTimeline() { Clear(); }

        public void Begin(double now, bool distant, double distanceMeters)
        {
            if (Active) ReplacedIncomplete++;
            Clear();
            Active = true; Distant = distant; Started = now;
            DistanceMeters = distanceMeters >= 0 && !double.IsInfinity(distanceMeters) ? distanceMeters : double.NaN;
        }

        public void Mark(TeleportMilestone milestone, double now)
        {
            if (!Active || now < Started) return;
            int index = (int)milestone;
            if (double.IsNaN(times[index])) times[index] = now;
        }

        public void Frame(double frameMs)
        {
            if (!Active) return;
            Frames++;
            if (frameMs > FrameMaxMs) FrameMaxMs = frameMs;
        }

        public void End(double now, TeleportOutcome outcome)
        {
            if (!Active) return;
            Active = false; Outcome = outcome; Ended = now >= Started ? now : Started;
        }

        // Milliseconds from the accepted teleport to a milestone; NaN when never seen.
        public double SinceStartMs(TeleportMilestone milestone)
        {
            double at = times[(int)milestone];
            return double.IsNaN(at) ? double.NaN : (at - Started) * 1000;
        }

        public double TotalMs => double.IsNaN(Ended) ? double.NaN : (Ended - Started) * 1000;

        // Time spent after the destination was ready (moved, area ready, floor found) and before
        // the native teleport ended: on a distant teleport, the fixed 8 s floor. NaN when a
        // readiness milestone was never observed, so a timeout is not reported as a floor.
        public double ReadyWaitMs
        {
            get
            {
                if (double.IsNaN(Ended)) return double.NaN;
                double ready = Math.Max(times[(int)TeleportMilestone.Moved],
                    Math.Max(times[(int)TeleportMilestone.AreaReady], times[(int)TeleportMilestone.FloorFound]));
                return double.IsNaN(ready) ? double.NaN : Math.Max(0, Ended - ready) * 1000;
            }
        }

        private void Clear()
        {
            for (int i = 0; i < times.Length; i++) times[i] = double.NaN;
            Active = false; Distant = false; DistanceMeters = double.NaN;
            Started = Ended = double.NaN; Outcome = TeleportOutcome.None; Frames = 0; FrameMaxMs = 0;
        }
    }
}
