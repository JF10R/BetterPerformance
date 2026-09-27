using System;
using BetterPerformance.Core;

internal static class FixedCatchupTests
{
    public static void Run()
    {
        // fixedDeltaTime 20 ms: maximumDeltaTime 0.2 s allows 10 steps, a 0.1 s cap allows 5.
        Check(FixedCatchupWindow.CapSteps(0.1, 0.02) == 5 && FixedCatchupWindow.CapSteps(0.2, 0.02) == 10, "cap steps as Unity computes them");
        Check(FixedCatchupWindow.CapSteps(0.1, 0) == 0, "no fixed step, no cap");

        var c = new FixedCatchupWindow();
        c.Note(1, 2, 5);                   // normal
        c.Note(2, 4, 5);                   // two steps is not a catch-up
        c.Note(3, 9, 5);                   // catch-up, under the cap
        c.Note(10, 40, 5);                 // spiral: 5 steps beyond the cap, half its fixed time
        c.Note(0, 5, 5);                   // no step: ignored
        c.Note(4, double.NaN, 5);          // unreadable time: ignored
        var s = c.Drain();
        Check(s.PairedFrames == 4 && s.PairedSteps == 16, "paired frames and steps");
        Check(s.CatchupFrames == 2 && s.ExtraSteps == 1 + 8, "catch-up frames and steps beyond two");
        Check(Near(s.FixedMsSum, 55) && Near(s.FixedMsMax, 40) && Near(s.CatchupFixedMsSum, 49), "fixed phase time");
        Check(s.StepsBeyondCap == 5 && Near(s.ProjectedSavedMs, 20), "what a 0.1 s cap would have skipped");
        Check(c.Drain().PairedFrames == 0, "drain resets");

        var b = new FrameBusyWindow();
        b.Note(33.3, 30);                  // 3.3 ms of work at a 30 Hz cap
        b.Note(33.3, 20);                  // 13.3 ms: over 120 and 144 Hz budgets
        b.Note(40, 0);                     // 40 ms: over every budget
        b.Note(10, 15);                    // wait larger than the loop clamps to zero work
        b.Note(double.NaN, 1);             // ignored
        var f = b.Drain();
        Check(f.Frames == 4 && f.Over60Hz == 1 && f.Over120Hz == 2 && f.Over144Hz == 2, "frames over each budget");
        Check(Near(f.BusyMsMax, 40) && Near(f.BusyMsSum, 3.3 + 13.3 + 40), "busy time");
        Check(Near(f.WaitMsSum, 30 + 20 + 0 + 10), "wait never exceeds the loop");
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-6;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
