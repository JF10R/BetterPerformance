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
            TerrainQueued = false, GrassReady = true, DungeonPending = false, ServerInformed = true, StableSeconds = 0.9,
        };
        Check(TeleportArrivalPolicy.Blocker(ready, minimum, settle) == ArrivalBlocker.None, "everything loaded ends the teleport");

        ArrivalBlocker With(Func<ArrivalState, ArrivalState> change) => TeleportArrivalPolicy.Blocker(change(ready), minimum, settle);
        Check(With(s => { s.TeleportSeconds = 2.0; return s; }) == ArrivalBlocker.Moving, "never before the native move");
        Check(With(s => { s.TeleportSeconds = double.NaN; return s; }) == ArrivalBlocker.Moving, "an unreadable timer never ends early");
        Check(With(s => { s.ActiveAreaLoaded = false; return s; }) == ArrivalBlocker.ActiveArea, "zones around the player first");
        Check(With(s => { s.ServerInformed = false; return s; }) == ArrivalBlocker.Server, "the server must know the destination before a quiet set means anything");
        Check(With(s => { s.ServerInformed = false; s.AreaReady = false; s.StableSeconds = 5; return s; }) == ArrivalBlocker.Server,
            "an empty, stable destination is not complete while the server streams the old area (2026-09-30: house missing)");
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

        // Stability counted only once the server could have answered the destination position.
        var gated = new TeleportArrivalPolicy.Settle();
        gated.Observe(0, 2.0);
        Check(double.IsNaN(gated.StableSeconds(3.0, double.NaN)), "no position sent: never stable");
        Check(double.IsNaN(gated.StableSeconds(2.2, 2.3)), "before the server could answer: not stable");
        Check(Near(gated.StableSeconds(3.0, 2.3), 0.7), "a set quiet since before the send counts from the send");
        gated.Observe(900, 2.6);
        Check(Near(gated.StableSeconds(3.0, 2.3), 0.4), "a change after the send restarts from the change");

        // Census: which category and distance band changed between checks.
        var census = new TeleportArrivalPolicy.SettleCensus();
        var changes = new long[TeleportArrivalPolicy.SettleCensus.Cells];
        census.Add(SettleCategory.Piece, 10);
        census.Add(SettleCategory.Creature, 40);
        census.Commit(changes);
        Check(census.StaticCount == 1 && census.TotalCount == 2, "static excludes creatures");
        Check(Sum(changes) == 0, "the first check is a baseline, not a change");
        census.Add(SettleCategory.Piece, 10);
        census.Add(SettleCategory.Creature, 70);
        census.Commit(changes);
        Check(census.StaticCount == 1, "a creature moving band leaves the static count alone");
        Check(changes[TeleportArrivalPolicy.SettleCensus.Cell(SettleCategory.Creature, 1)] == 1 &&
              changes[TeleportArrivalPolicy.SettleCensus.Cell(SettleCategory.Creature, 2)] == 1 && Sum(changes) == 2,
            "the creature's old and new band both changed");
        census.Add(SettleCategory.Piece, 10);
        census.Add(SettleCategory.Piece, 100);
        census.Add(SettleCategory.Creature, 70);
        census.Commit(changes);
        Check(census.StaticCount == 2 && changes[TeleportArrivalPolicy.SettleCensus.Cell(SettleCategory.Piece, 2)] == 1, "a far piece arriving is recorded in its band");
        Check(TeleportArrivalPolicy.SettleCensus.Band(31.9) == 0 && TeleportArrivalPolicy.SettleCensus.Band(32) == 1 &&
              TeleportArrivalPolicy.SettleCensus.Band(64) == 2, "band edges");
        Check(!TeleportArrivalPolicy.IsDynamic(SettleCategory.Static) && TeleportArrivalPolicy.IsDynamic(SettleCategory.Item), "item drops are dynamic");
        census.Reset();
        Check(census.StaticCount == 0 && census.TotalCount == 0, "reset clears the census");
    }

    private static long Sum(long[] values)
    {
        long total = 0;
        foreach (long value in values) total += value;
        return total;
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
