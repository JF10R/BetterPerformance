using System;
using BetterPerformance.Core;

internal static class PrecompressionPolicyTests
{
    public static void Run()
    {
        QuietWindow();
        UnchangedState();
        InFlightGuard();
        MinimumInterval();
        RateCap();
        ResetReleasesState();
    }

    // A moving change counter restarts the quiet window; only stillness dispatches.
    private static void QuietWindow()
    {
        var policy = new PrecompressionPolicy(2.0, 0.0, 10);
        Check(policy.Evaluate(0.0, 1) == PrecompressionDecision.Dirty, "first observation starts the quiet window");
        Check(policy.Evaluate(1.9, 1) == PrecompressionDecision.Dirty, "quiet window not yet elapsed");
        Check(policy.Evaluate(1.95, 2) == PrecompressionDecision.Dirty, "a change restarts the window");
        Check(policy.Evaluate(3.9, 2) == PrecompressionDecision.Dirty, "restarted window measured from the change");
        Check(policy.Evaluate(3.95, 2) == PrecompressionDecision.Run, "quiet map dispatches");
    }

    // Re-running on state a run already covered would produce the same entry.
    private static void UnchangedState()
    {
        var policy = new PrecompressionPolicy(1.0, 0.0, 10);
        Check(policy.Evaluate(0.0, 7) == PrecompressionDecision.Dirty, "window starts");
        Check(policy.Evaluate(2.0, 7) == PrecompressionDecision.Run, "quiet dispatches");
        policy.Dispatch(2.0, 7);
        policy.Complete(produced: true);
        policy.Adopted();
        Check(policy.Evaluate(50.0, 7) == PrecompressionDecision.Unchanged, "identical state never re-runs");
        Check(policy.Evaluate(51.0, 8) == PrecompressionDecision.Dirty, "a later change reopens the window");
        Check(policy.Evaluate(53.0, 8) == PrecompressionDecision.Run, "changed state runs again");
    }

    // At most one snapshot in flight, and the published result must be adopted before the next.
    private static void InFlightGuard()
    {
        var policy = new PrecompressionPolicy(0.0, 0.0, 10);
        Check(policy.Evaluate(0.0, 1) == PrecompressionDecision.Run, "zero quiet window dispatches immediately");
        policy.Dispatch(0.0, 1);
        Check(policy.InFlight, "dispatch marks the run in flight");
        Check(policy.Evaluate(1.0, 2) == PrecompressionDecision.InFlight, "a second run is refused while one is in flight");
        bool rejected = false;
        try { policy.Dispatch(1.0, 2); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "concurrent dispatch is a programming error, not a silent second run");
        policy.Complete(produced: true);
        Check(!policy.InFlight && policy.ResultPending, "a produced result waits for the main thread");
        Check(policy.Evaluate(2.0, 2) == PrecompressionDecision.InFlight, "an unadopted result blocks the next run");
        policy.Adopted();
        Check(!policy.ResultPending && policy.Evaluate(3.0, 2) == PrecompressionDecision.Run, "adoption frees the slot");
        policy.Dispatch(3.0, 2);
        policy.Complete(produced: false);
        Check(!policy.InFlight && !policy.ResultPending, "a failed run parks nothing and frees the slot");
    }

    private static void MinimumInterval()
    {
        var policy = new PrecompressionPolicy(0.0, 30.0, 10);
        policy.Evaluate(0.0, 1);
        policy.Dispatch(0.0, 1);
        policy.Complete(produced: false);
        Check(policy.Evaluate(29.9, 2) == PrecompressionDecision.TooSoon, "minimum interval holds a changed map back");
        Check(policy.Evaluate(30.0, 2) == PrecompressionDecision.Run, "interval elapsed releases the run");
    }

    // The cap is a rolling minute, not a fixed bucket.
    private static void RateCap()
    {
        var policy = new PrecompressionPolicy(0.0, 0.0, 2);
        for (int i = 1; i <= 2; i++)
        {
            Check(policy.Evaluate(i, i) == PrecompressionDecision.Run, "run " + i + " within the cap");
            policy.Dispatch(i, i);
            policy.Complete(produced: false);
        }
        Check(policy.Evaluate(3.0, 3) == PrecompressionDecision.RateLimited, "third run in the same minute is refused");
        Check(policy.Evaluate(60.5, 3) == PrecompressionDecision.RateLimited, "the older run has not aged out yet");
        Check(policy.Evaluate(61.5, 3) == PrecompressionDecision.Run, "the window rolls and one slot returns");
        policy.Dispatch(61.5, 3);
        policy.Complete(produced: false);
        Check(policy.Evaluate(61.9, 4) == PrecompressionDecision.RateLimited, "the cap still counts the second run until it ages out");
        Check(policy.Evaluate(62.0, 4) == PrecompressionDecision.Run, "a run exactly one minute old no longer counts");
    }

    private static void ResetReleasesState()
    {
        var policy = new PrecompressionPolicy(0.0, 30.0, 1);
        policy.Evaluate(0.0, 1);
        policy.Dispatch(0.0, 1);
        policy.Complete(produced: true);
        Check(policy.ResultPending, "result parked before reset");
        policy.Reset();
        Check(!policy.InFlight && !policy.ResultPending, "a new world drops the previous world's in-flight state");
        Check(policy.Evaluate(1.0, 1) == PrecompressionDecision.Run, "reset clears the interval and the cap");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
