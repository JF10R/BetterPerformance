using System;
using System.Linq;
using BetterPerformance.Core;

internal static class SpawnTallyTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var tally = new SpawnTally(2);
        int wolf = tally.Register("Wolf"), golem = tally.Register("StoneGolem");
        Check(wolf != SpawnTally.OtherSlot && golem != wolf && tally.Register("Wolf") == wolf, "Names map to stable slots.");
        Check(tally.Register("Fenring") == SpawnTally.OtherSlot && tally.Register(null) == SpawnTally.OtherSlot,
            "Full or unnamed registrations fall into other.");
        int fenring = tally.Register("Fenring");

        tally.Add(wolf, SpawnCounter.CapChecks);
        tally.Add(wolf, SpawnCounter.CapChecks);
        tally.Add(wolf, SpawnCounter.CapChecks);
        tally.Add(wolf, SpawnCounter.PointSearches);
        tally.Add(wolf, SpawnCounter.CappedPointSearches);
        tally.Add(wolf, SpawnCounter.PointSearchFailed);
        for (int i = 0; i < 20; i++) tally.Add(wolf, SpawnCounter.PointsRejected);
        for (int i = 0; i < 12; i++) tally.Add(wolf, SpawnCounter.PointsInPlayerBase);
        tally.Add(golem, SpawnCounter.NaturalDay);
        tally.Add(golem, SpawnCounter.PointSearches);
        tally.Add(fenring, SpawnCounter.NaturalNight);
        tally.Add(fenring, SpawnCounter.NaturalNight);
        tally.Add(99, SpawnCounter.Event);
        tally.Add(golem, (SpawnCounter)42);

        SpawnRow[] rows = tally.Drain(8);
        SpawnRow w = rows.Single(r => r.Name == "Wolf");
        Check(w.CapRefused == 2 && w.PointSearches == 1 && w.PointSearchFailed == 1,
            "Cap refusals are cap checks not followed by a capped point search.");
        Check(w.PointsRejected == 20 && w.PointsInPlayerBase == 12, "Point rejections keep the player-base subset.");
        SpawnRow other = rows.Single(r => r.Name == "other");
        Check(other.NaturalNight == 2 && other.Event == 1, "Overflow and unknown slots aggregate into other.");
        Check(rows.Last().Name == "other" && rows[0].Name == "Wolf", "Busiest named row first, other last.");
        Check(rows.Single(r => r.Name == "StoneGolem").NaturalDay == 1, "Invalid counters are ignored, valid ones kept.");

        Check(tally.Drain(8).Length == 0, "Drain clears interval counts.");
        Check(tally.Register("StoneGolem") == golem, "Drain keeps registrations.");

        tally.Add(wolf, SpawnCounter.PointSearches);
        tally.Add(wolf, SpawnCounter.PointSearches);
        tally.Add(wolf, SpawnCounter.CappedPointSearches);
        tally.Add(golem, SpawnCounter.NaturalDay);
        SpawnRow[] folded = tally.Drain(1);
        Check(folded.Length == 2 && folded[0].Name == "Wolf" && folded[1].Name == "other" && folded[1].NaturalDay == 1,
            "Rows past topN fold into other.");
        Check(folded[0].CapRefused == 0, "A search without a cap check never yields a negative refusal.");

        tally.Reset();
        Check(tally.RegisteredNames == 0 && tally.Register("Fenring") == 1, "Reset clears registrations.");
        Check(SpawnTally.GaugeKey("Draugr_Elite (1)") == "draugr_elite__1_" && SpawnTally.GaugeKey("") == "unnamed",
            "Gauge keys are sanitized.");
        bool threw = false;
        try { new SpawnTally(0); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "Capacity is bounded.");
    }
}
