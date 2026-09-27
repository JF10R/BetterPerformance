using System;

namespace BetterPerformance.Core
{
    public struct PostArrivalSnapshot
    {
        public double DurationMs, MaxFrameMs, SlowFrameMsSum, NearDrainedMs;
        public long Frames, SlowFrames, VerySlowFrames, SlowGpuBound, SlowCpuBound;
        public int NearPendingAtArrival, DistantPendingAtArrival;
        public bool Cut;
    }

    // The first seconds of play after a teleport's loading screen: slow frames, whether the
    // engine counters point at the GPU or the main thread, and how long the objects still
    // waiting for creation near the player take to drain. docs/teleport-loading.md.
    public sealed class PostArrivalWindow
    {
        public const double SlowFrameMs = 50, VerySlowFrameMs = 100;
        private readonly double windowSeconds;
        private double started = double.NaN, last;
        private PostArrivalSnapshot current;
        private bool pendingSeen;

        public PostArrivalWindow(double windowSeconds = 10) { this.windowSeconds = windowSeconds; }

        public bool Active { get; private set; }

        public void Begin(double now)
        {
            current = new PostArrivalSnapshot { NearDrainedMs = double.NaN, NearPendingAtArrival = -1, DistantPendingAtArrival = -1 };
            started = last = now;
            pendingSeen = false;
            Active = true;
        }

        // Objects not yet created in the scene's near and distant lists; the first count is the arrival's.
        public void Pending(double now, int near, int distant)
        {
            if (!Active || now < started) return;
            if (!pendingSeen)
            {
                pendingSeen = true;
                current.NearPendingAtArrival = near;
                current.DistantPendingAtArrival = distant;
            }
            if (near == 0 && double.IsNaN(current.NearDrainedMs)) current.NearDrainedMs = (now - started) * 1000;
        }

        // mainMs and gpuMs are the engine's last completed frame (NaN when unavailable).
        // Returns true on the frame that closes the window.
        public bool Frame(double now, double frameMs, double mainMs, double gpuMs)
        {
            if (!Active) return false;
            last = now;
            current.Frames++;
            if (frameMs > current.MaxFrameMs) current.MaxFrameMs = frameMs;
            if (frameMs >= SlowFrameMs)
            {
                current.SlowFrames++;
                current.SlowFrameMsSum += frameMs;
                if (frameMs >= VerySlowFrameMs) current.VerySlowFrames++;
                if (gpuMs >= SlowFrameMs) current.SlowGpuBound++;
                else if (mainMs >= SlowFrameMs) current.SlowCpuBound++;
            }
            if (now - started < windowSeconds) return false;
            Close(false);
            return true;
        }

        // A new teleport before the window ends closes it early.
        public void Cut(double now)
        {
            if (!Active) return;
            if (now > last) last = now;
            Close(true);
        }

        public PostArrivalSnapshot Result { get; private set; }

        private void Close(bool cut)
        {
            current.DurationMs = Math.Max(0, last - started) * 1000;
            current.Cut = cut;
            Result = current;
            Active = false;
        }
    }
}
