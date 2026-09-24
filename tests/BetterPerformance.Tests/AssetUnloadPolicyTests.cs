using System;
using BetterPerformance.Core;

internal static class AssetUnloadPolicyTests
{
    public static void Run()
    {
        const double cap = 7200;
        Check(AssetUnloadPolicy.Periodic(1800, cap, false, 1) == AssetUnloadDecision.NativeSkips, "a recent unload leaves the native skip alone");
        Check(AssetUnloadPolicy.Periodic(3599, cap, true, 0) == AssetUnloadDecision.NativeSkips, "the native boundary is exclusive, as in vanilla");
        Check(AssetUnloadPolicy.Periodic(3600, cap, false, 1) == AssetUnloadDecision.Defer, "a client in play defers");
        Check(AssetUnloadPolicy.Periodic(3600, cap, false, 0) == AssetUnloadDecision.Defer, "a client defers even alone: its natural pauses unload");
        Check(AssetUnloadPolicy.Periodic(3600, cap, true, 2) == AssetUnloadDecision.Defer, "a server with players defers");
        Check(AssetUnloadPolicy.Periodic(3600, cap, true, 0) == AssetUnloadDecision.RunNative, "an empty server unloads now");
        Check(AssetUnloadPolicy.Periodic(7200, cap, true, 2) == AssetUnloadDecision.RunCapped, "the cap wins over players");
        Check(AssetUnloadPolicy.Periodic(9000, cap, false, 1) == AssetUnloadDecision.RunCapped, "the cap wins on a client");
        Check(AssetUnloadPolicy.Periodic(double.NaN, cap, true, 0) == AssetUnloadDecision.NativeSkips, "an unreadable age never forces an unload");
        Check(Throws(() => AssetUnloadPolicy.Periodic(4000, 3000, false, 0)), "a cap below the native period is refused");

        Check(AssetUnloadPolicy.RunDeferredOnServer(true, 4000, 0), "a pending unload runs when the server empties");
        Check(!AssetUnloadPolicy.RunDeferredOnServer(true, 4000, 1), "not while a peer is connected or joining");
        Check(!AssetUnloadPolicy.RunDeferredOnServer(false, 4000, 0), "nothing pending, nothing to run");
        Check(!AssetUnloadPolicy.RunDeferredOnServer(true, 100, 0), "an unload that already happened clears the need");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
