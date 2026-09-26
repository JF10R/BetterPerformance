using System;
using BetterPerformance.Core;

internal static class TeleportArrivalPolicyTests
{
    public static void Run()
    {
        const double minimum = 3, settle = 0.75;
        var ready = new ArrivalState
        {
            TeleportSeconds = 3.4, ActiveAreaLoaded = true, AreaReady = true, FloorFound = true,
            TerrainQueued = false, GrassReady = true, DungeonPending = false, StableSeconds = 0.9,
        };
        Check(TeleportArrivalPolicy.Blocker(ready, minimum, settle) == ArrivalBlocker.None, "everything loaded ends the teleport");

        ArrivalBlocker With(Func<ArrivalState, ArrivalState> change) => TeleportArrivalPolicy.Blocker(change(ready), minimum, settle);
        Check(With(s => { s.TeleportSeconds = 2.0; return s; }) == ArrivalBlocker.Moving, "never before the native move");
        Check(With(s => { s.TeleportSeconds = double.NaN; return s; }) == ArrivalBlocker.Moving, "an unreadable timer never ends early");
        Check(With(s => { s.ActiveAreaLoaded = false; return s; }) == ArrivalBlocker.ActiveArea, "zones around the player first");
        Check(With(s => { s.AreaReady = false; return s; }) == ArrivalBlocker.Objects, "every received nearby object instantiated");
        Check(With(s => { s.FloorFound = false; return s; }) == ArrivalBlocker.Floor, "ground under the target");
        Check(With(s => { s.TerrainQueued = true; return s; }) == ArrivalBlocker.Terrain, "no queued terrain rebuild nearby");
        Check(With(s => { s.GrassReady = false; return s; }) == ArrivalBlocker.Grass, "grass generated around the player");
        Check(With(s => { s.DungeonPending = true; return s; }) == ArrivalBlocker.Dungeon, "no dungeon still being sliced in");
        Check(With(s => { s.StableSeconds = 0.5; return s; }) == ArrivalBlocker.Settling, "the server's objects stopped arriving");
        Check(With(s => { s.StableSeconds = double.NaN; return s; }) == ArrivalBlocker.Settling, "an unobserved set is not settled");
        Check(With(s => { s.TeleportSeconds = 2.9; return s; }) == ArrivalBlocker.Minimum, "the configured minimum holds last");

        var settleClock = new TeleportArrivalPolicy.Settle();
        Check(double.IsNaN(settleClock.StableSeconds(1)), "nothing observed yet");
        settleClock.Observe(10, 1.0);
        settleClock.Observe(10, 1.5);
        Check(Near(settleClock.StableSeconds(1.8), 0.8), "an unchanged count keeps its first time");
        settleClock.Observe(42, 2.0);
        Check(Near(settleClock.StableSeconds(2.3), 0.3), "a change restarts the clock");
        settleClock.Observe(40, 2.4);
        Check(Near(settleClock.StableSeconds(2.4), 0), "a drop is a change too");
        Check(double.IsNaN(settleClock.StableSeconds(2.0)), "a time before the change is refused");
        settleClock.Reset();
        Check(double.IsNaN(settleClock.StableSeconds(3)), "reset forgets the count");
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
