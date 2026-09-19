using System;
using BetterPerformance.Core;

internal static class SendWindowControllerTests
{
    // Defaults under test: min 16384, max 262144, tick 0.05 s, margin 1.5, default 32768.
    public static void TargetFormulaAndClamps()
    {
        var controller = new SendWindowController();

        Check(controller.Window(7) == 32768, "an unknown peer reports the default window");
        Check(controller.Update(7, 0, 20, 0, 0) == 32768, "no Steam rate estimate yields the default window");
        Check(controller.Update(8, -1, 20, 0, 0) == 32768, "a negative rate is read as no estimate");
        Check(controller.Window(7) == 32768, "the default is retained as the peer's window");

        // 1 MB/s at 1 ms: 1048576 x (0.001 + 0.05) x 1.5 = 80216 B.
        int fast = controller.Update(1, 1048576, 1, 0, 0);
        Check(fast >= 80000 && fast <= 80500, "bandwidth-delay product at 1 MB/s and 1 ms is about 78 KB, got " + fast);

        // 250 KB/s at 40 ms: 256000 x (0.04 + 0.05) x 1.5 = 34560 B.
        int typical = controller.Update(2, 256000, 40, 0, 0);
        Check(typical >= 34400 && typical <= 34700, "250 KB/s at 40 ms is about 34 KB, got " + typical);

        Check(controller.Update(3, 100 * 1048576, 20, 0, 0) == 262144, "a fast link is capped at maxBytes");
        Check(controller.Update(4, 1024, 20, 0, 0) == 16384, "a slow link is floored at minBytes");

        // A negative ping is read as zero: 1048576 x 0.05 x 1.5 = 78643 B.
        int noPing = controller.Update(5, 1048576, -5, -1, 0);
        Check(noPing >= 78500 && noPing <= 78800, "a negative ping and backlog are read as zero, got " + noPing);

        var summary = controller.Drain();
        Check(summary.UnknownRate == 2, "both missing rate estimates are counted, got " + summary.UnknownRate);
        Check(summary.Updates == 7, "every update on a tracked peer is counted, got " + summary.Updates);
        Check(summary.WindowMin == 16384 && summary.WindowMax == 262144, "the interval reports the extremes it returned");
        Check(summary.Backoffs == 0 && summary.Recoveries == 0, "a drained interval without backlog has no back-off");
    }

    public static void BackoffHoldAndRecovery()
    {
        var controller = new SendWindowController();
        Check(controller.Update(1, 1048576, 1, 0, 0.0) == 80216, "the first window for a peer is the target outright");

        Check(controller.Update(1, 1048576, 1, 50000, 0.10) == 80216, "one over-half sample does not back off");
        Check(controller.Update(1, 1048576, 1, 50000, 0.15) == 80216, "two samples less than 0.2 s apart do not back off");
        Check(controller.Update(1, 1048576, 1, 50000, 0.40) == 40108, "two over-half samples 0.2 s apart halve the window");

        Check(controller.Update(1, 1048576, 1, 0, 0.50) == 40108, "growth is held right after a back-off");
        Check(controller.Update(1, 1048576, 1, 0, 2.30) == 40108, "the hold lasts two seconds");
        Check(controller.Update(1, 1048576, 1, 0, 2.50) == 50135, "growth past the hold is at most 25 % of the window");
        Check(controller.Update(1, 1048576, 1, 0, 2.60) == 62668, "growth stays bounded on the next update");

        int window = 0;
        for (int i = 0; i < 8; i++) window = controller.Update(1, 1048576, 1, 0, 2.70 + i * 0.1);
        Check(window == 80216, "the window converges back to the target, got " + window);

        var summary = controller.Drain();
        Check(summary.Backoffs == 1, "exactly one back-off, got " + summary.Backoffs);
        Check(summary.Recoveries == 1, "reaching the target after a back-off counts one recovery, got " + summary.Recoveries);
        Check(summary.WindowMin == 40108 && summary.WindowMax == 80216, "the interval extremes span the back-off");

        // A peer already at the floor cannot be halved below it.
        Check(controller.Update(2, 1024, 20, 0, 0.0) == 16384, "a slow peer starts at the floor");
        controller.Update(2, 1024, 20, 10000, 0.10);
        Check(controller.Update(2, 1024, 20, 10000, 0.40) == 16384, "a back-off never goes below minBytes");
        Check(controller.Drain().Backoffs == 1, "the floored back-off is still counted");

        // A target below the window is applied at once; only growth is rationed.
        controller.Update(3, 1048576, 1, 0, 0.0);
        Check(controller.Update(3, 256000, 40, 0, 0.1) == 34560, "a shrinking target applies immediately");
        controller.Drain();
    }

    public static void TimeValidationCapacityAndDrain()
    {
        var controller = new SendWindowController();
        controller.Update(1, 1048576, 1, 0, 10.0);
        controller.Update(1, 1048576, 1, 50000, 10.0);
        Check(controller.Update(1, 1048576, 1, 50000, 5.0) == 80216, "backwards time is read as the same time, not as elapsed");
        Check(controller.Update(1, 1048576, 1, 50000, double.NaN) == 80216, "a non-finite clock is read as the same time");
        Check(controller.Update(1, 1048576, 1, 50000, double.PositiveInfinity) == 80216, "an infinite clock is read as the same time");
        Check(controller.Update(1, 1048576, 1, 50000, 10.3) == 40108, "real elapsed time after the clamped samples backs off");
        controller.Drain();

        Check(controller.Peers == 1, "one peer is tracked");
        controller.Forget(1);
        Check(controller.Peers == 0 && controller.Window(1) == 32768, "a forgotten peer is untracked and reports the default");

        var capped = new SendWindowController();
        for (int i = 0; i < 64; i++) capped.Update(i, 256000, 40, 0, 0);
        Check(capped.Peers == 64, "the peer table fills to the cap");
        Check(capped.Update(999, 1048576, 1, 0, 0) == 32768, "a peer over the cap is served the default window");
        Check(capped.Peers == 64, "a peer over the cap is not stored");
        var summary = capped.Drain();
        Check(summary.PeersOverCapacity == 1, "the refused peer is counted, got " + summary.PeersOverCapacity);
        Check(summary.Updates == 64, "an over-capacity call is not counted as an update, got " + summary.Updates);
        Check(summary.Peers == 64, "the drain reports the live peer count");

        var empty = capped.Drain();
        Check(empty.Updates == 0 && empty.Backoffs == 0 && empty.Recoveries == 0 && empty.UnknownRate == 0
            && empty.PeersOverCapacity == 0, "Drain resets every interval counter");
        Check(empty.WindowMin == 0 && empty.WindowMax == 0, "an interval with no update reports 0/0 window extremes");
        Check(empty.Peers == 64, "the peer count is state, not an interval counter");

        for (int i = 0; i < 1000; i++) capped.Update(3, 256000, 40, 0, i * 0.01);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { capped.Update(3, 256000, 40, 0, 10 + i * 0.01); capped.Window(3); }
        capped.Drain();
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "the send window must allocate no managed bytes after warm-up");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
