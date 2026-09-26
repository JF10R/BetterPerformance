using System;
using BetterPerformance.Core;

internal static class TeleportTimelineTests
{
    public static void Run()
    {
        // A distant portal: moved at 2 s, area ready at 3.5 s, floor at 3.6 s, native end at 8.02 s.
        var t = new TeleportTimeline();
        t.Begin(100, true, 1500);
        t.Mark(TeleportMilestone.ZoneLoaded, 100.4);
        t.Mark(TeleportMilestone.Moved, 102);
        t.Mark(TeleportMilestone.ActiveAreaLoaded, 102.9);
        t.Mark(TeleportMilestone.AreaReady, 103.5);
        t.Mark(TeleportMilestone.AreaReady, 104);
        t.Mark(TeleportMilestone.FloorFound, 103.6);
        t.Frame(16); t.Frame(240); t.Frame(8);
        Check(t.Active, "active until the native end");
        t.End(108.02, TeleportOutcome.Arrived);
        Check(!t.Active && t.Outcome == TeleportOutcome.Arrived, "ended as arrived");
        Check(Near(t.TotalMs, 8020), "total from TeleportTo to the end");
        Check(Near(t.SinceStartMs(TeleportMilestone.AreaReady), 3500), "a milestone keeps its first time");
        Check(Near(t.SinceStartMs(TeleportMilestone.ZoneLoaded), 400), "a target zone already loaded is seen before the move");
        Check(Near(t.ReadyWaitMs, 4420), "the wait after readiness is the fixed floor");
        Check(t.Frames == 3 && Near(t.FrameMaxMs, 240), "frames and the slowest one");
        Check(Near(t.DistanceMeters, 1500), "distance kept");

        // Ready only after the floor: nothing is waited for the floor.
        var late = new TeleportTimeline();
        late.Begin(0, true, 900);
        late.Mark(TeleportMilestone.Moved, 2);
        late.Mark(TeleportMilestone.FloorFound, 2.1);
        late.Mark(TeleportMilestone.AreaReady, 11);
        late.End(11, TeleportOutcome.Arrived);
        Check(Near(late.ReadyWaitMs, 0), "a late area pays no floor wait");

        // Never ready (15 s timeout or teleport cut short): no floor wait is claimed.
        var never = new TeleportTimeline();
        never.Begin(0, true, 900);
        never.Mark(TeleportMilestone.Moved, 2);
        never.End(15.1, TeleportOutcome.Arrived);
        Check(double.IsNaN(never.ReadyWaitMs) && double.IsNaN(never.SinceStartMs(TeleportMilestone.AreaReady)), "an unseen milestone is NaN, not zero");

        // A new teleport replaces an unfinished one; marks outside a teleport are ignored.
        var replaced = new TeleportTimeline();
        replaced.Mark(TeleportMilestone.Moved, 1);
        replaced.Frame(50);
        Check(replaced.Frames == 0 && double.IsNaN(replaced.TotalMs), "nothing recorded while idle");
        replaced.Begin(0, false, 10);
        replaced.Begin(5, true, 20);
        Check(replaced.ReplacedIncomplete == 1 && replaced.Distant && Near(replaced.Started, 5), "an unfinished teleport is replaced and counted");
        replaced.Mark(TeleportMilestone.Moved, 4);
        Check(double.IsNaN(replaced.SinceStartMs(TeleportMilestone.Moved)), "a time before the start is refused");
        replaced.End(3, TeleportOutcome.Aborted);
        Check(Near(replaced.TotalMs, 0) && replaced.Outcome == TeleportOutcome.Aborted, "an end before the start clamps to zero");
        var bad = new TeleportTimeline();
        bad.Begin(0, true, double.PositiveInfinity);
        Check(double.IsNaN(bad.DistanceMeters), "an unreadable distance is NaN");
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-6;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
