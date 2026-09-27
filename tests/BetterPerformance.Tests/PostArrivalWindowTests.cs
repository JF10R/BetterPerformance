using System;
using BetterPerformance.Core;

internal static class PostArrivalWindowTests
{
    public static void Run()
    {
        // Arrival at t=100: 300 objects pending near, 40 distant; drained at 101.5; window closes at 110.
        var w = new PostArrivalWindow(10);
        Check(!w.Frame(99, 500, 0, 0), "nothing recorded before Begin");
        w.Begin(100);
        w.Pending(100.1, 300, 40);
        w.Pending(100.6, 120, 40);
        w.Pending(101.5, 0, 12);
        w.Pending(102, 0, 0);
        Check(!w.Frame(100.2, 16, 10, 12), "a normal frame");
        Check(!w.Frame(100.4, 80, 75, 20), "a slow frame on the main thread");
        Check(!w.Frame(100.6, 120, 30, 110), "a very slow frame on the GPU");
        Check(!w.Frame(100.8, 60, double.NaN, double.NaN), "a slow frame without counters");
        Check(w.Frame(110, 16, 10, 12), "the frame at 10 s closes the window");
        Check(!w.Active, "closed");
        var r = w.Result;
        Check(r.Frames == 5 && r.SlowFrames == 3 && r.VerySlowFrames == 1, "slow and very slow frames");
        Check(r.SlowCpuBound == 1 && r.SlowGpuBound == 1, "GPU wins over main thread, unknown is neither");
        Check(Near(r.MaxFrameMs, 120) && Near(r.SlowFrameMsSum, 260), "max and slow sum");
        Check(r.NearPendingAtArrival == 300 && r.DistantPendingAtArrival == 40, "the first count is the arrival's");
        Check(Near(r.NearDrainedMs, 1500), "near objects drained 1.5 s after arrival");
        Check(Near(r.DurationMs, 10000) && !r.Cut, "full window");

        // A new teleport cuts the window; never drained and never counted stay unknown.
        var cut = new PostArrivalWindow(10);
        cut.Begin(0);
        cut.Frame(1, 20, 0, 0);
        cut.Cut(2.5);
        Check(!cut.Active && cut.Result.Cut && Near(cut.Result.DurationMs, 2500), "cut early");
        Check(double.IsNaN(cut.Result.NearDrainedMs) && cut.Result.NearPendingAtArrival == -1, "no count means unknown, not zero");
        cut.Pending(3, 0, 0);
        Check(double.IsNaN(cut.Result.NearDrainedMs), "a closed window ignores counts");
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-6;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
