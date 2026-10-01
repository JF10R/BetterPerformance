using System;
using System.Collections.Generic;
using System.Linq;
using BetterPerformance.Core;

internal static class HeightmapCriticalProfileTests
{
    private static readonly HeightmapRebuildSettings Defaults = new HeightmapRebuildSettings
    { BudgetMs = 4, CostEstimateMs = 5, MaxDeferMs = 500, FrameMs = 16 };

    public static void Run()
    {
        // Buckets: 0 is on the square; upper edges inclusive like the critical test (<= radius).
        Check(HeightmapCriticalProfile.Bucket(0) == 0 && HeightmapCriticalProfile.Bucket(-1) == 0 &&
            HeightmapCriticalProfile.Bucket(0.01) == 1 && HeightmapCriticalProfile.Bucket(16) == 1 &&
            HeightmapCriticalProfile.Bucket(16.01) == 2 && HeightmapCriticalProfile.Bucket(64) == 4 &&
            HeightmapCriticalProfile.Bucket(80) == 5 && HeightmapCriticalProfile.Bucket(80.01) == 6 &&
            HeightmapCriticalProfile.Bucket(double.NaN) == 6, "Distance buckets and their inclusive edges.");
        Check(HeightmapCriticalProfile.AgeBucket(1000, true) == 0 && HeightmapCriticalProfile.AgeBucket(1001, true) == 1 &&
            HeightmapCriticalProfile.AgeBucket(10001, true) == 2 && HeightmapCriticalProfile.AgeBucket(double.PositiveInfinity, false) == 2 &&
            HeightmapCriticalProfile.AgeBucket(20000, false) == 2 && HeightmapCriticalProfile.AgeBucket(5000, false) == 3 &&
            HeightmapCriticalProfile.AgeBucket(double.NaN, false) == 3, "An inexact age is older only when its lower bound says so.");

        // Aggregation: frame 10 has three rebuilds (on, 20 m, 70 m), frame 11 one at 90 m.
        var profile = new HeightmapCriticalProfile();
        profile.Observe(10, 0, 8, HeightmapCriticalClause.Player, HeightmapRebuildContext.AfterArrival, 300, true, true);
        profile.Observe(10, 20, 6, HeightmapCriticalClause.Player, HeightmapRebuildContext.AfterArrival, 300, true, true);
        profile.Observe(10, 70, 5, HeightmapCriticalClause.Player, HeightmapRebuildContext.AfterArrival, 5000, true, true);
        profile.Observe(11, 90, 7, HeightmapCriticalClause.Camera, HeightmapRebuildContext.None, double.NaN, false, false);
        var gauges = new List<NumberValue>();
        profile.Sample(gauges);
        double G(string name) => gauges.Single(g => g.Name == HeightmapCriticalProfile.Prefix + name).Value;
        Check(G("on_square_count") == 1 && G("on_square_ms_sum") == 8 && G("16_32_count") == 1 && G("64_80_ms_max") == 5 &&
            G("80_plus_count") == 1 && G("0_16_count") == 0, "Counts and costs land in their distance bucket.");
        Check(G("frames") == 2 && G("frame_count_max") == 3 && G("worst_frame_ms") == 19 && G("worst_frame_count") == 3,
            "The worst frame splits into its rebuild count and summed cost.");
        Check(G("by_player") == 3 && G("by_camera") == 1 && G("by_distant_lod") == 0 && G("unplanned") == 1,
            "Clause and unplanned counts.");
        Check(G("ctx_after_arrival_count") == 3 && G("ctx_after_arrival_ms_sum") == 19 && G("ctx_none_count") == 1 &&
            G("ctx_teleporting_count") == 0, "Context counts and costs.");
        Check(G("age_1s_count") == 2 && G("age_10s_count") == 1 && G("age_unknown_count") == 1 && G("age_older_count") == 0 &&
            G("age_1s_ms_sum") == 14, "Enable-age counts and costs.");
        Check(gauges.Count == 43 && gauges.Select(g => g.Name).Distinct().Count() == 43, "43 distinct gauges.");
        gauges.Clear();
        profile.Sample(gauges);
        Check(gauges.Count == 43 && gauges.All(g => g.Value == 0), "Sampling resets; an idle interval exports zeros.");

        // The log: exact ages, a lower bound after eviction, "older" while nothing was evicted.
        var log = new HeightmapEnableLog(3);
        log.Note(1, 1000); log.Note(2, 2000);
        Check(log.AgeMs(2, 5000, 1000, out bool exact) == 3000 && exact, "An exact age since the last enable.");
        Check(double.IsPositiveInfinity(log.AgeMs(9, 5000, 1000, out exact)) && !exact, "Missing before any eviction: older than tracking.");
        log.Note(3, 3000); log.Note(1, 4000);
        Check(log.AgeMs(1, 5000, 1000, out exact) == 1000 && exact, "A re-enable counts from the newest entry.");
        Check(log.AgeMs(9, 5000, 1000, out exact) == 3000 && !exact, "Missing once full: at least the oldest retained age.");

        // Zero behaviour change: the plan with the observer fed from it equals the plan without.
        var watched = Candidates();
        var control = Candidates();
        var planA = HeightmapRebuildPlanner.Plan(watched, watched.Length, new int[watched.Length], Defaults);
        var observer = new HeightmapCriticalProfile();
        for (int i = 0; i < watched.Length; i++)
            if (watched[i].Run && watched[i].Critical)
                observer.Observe(1, watched[i].DistanceM, 6, HeightmapCriticalClause.Player, HeightmapRebuildContext.None, 0, true, true);
        var planB = HeightmapRebuildPlanner.Plan(control, control.Length, new int[control.Length], Defaults);
        Check(planA.Critical == planB.Critical && planA.Overdue == planB.Overdue && planA.Budgeted == planB.Budgeted &&
            planA.Deferred == planB.Deferred && planA.AllowanceMs == planB.AllowanceMs &&
            watched.Zip(control, (a, b) => a.Reason == b.Reason && a.Id == b.Id && a.DistanceM == b.DistanceM &&
                a.AgeMs == b.AgeMs && a.Critical == b.Critical).All(x => x), "Observing critical rebuilds leaves the plan unchanged.");
        var replan = Candidates();
        var planC = HeightmapRebuildPlanner.Plan(replan, replan.Length, new int[replan.Length], Defaults);
        Check(planC.AllowanceMs == planA.AllowanceMs && replan.Select(c => c.Reason).SequenceEqual(watched.Select(c => c.Reason)),
            "The next plan on the same queue is unchanged after observation.");
        Check(planA.Critical == 3 && planA.Budgeted >= 1, "The fixture exercises critical and optional rebuilds.");

        // Allocation-free once warm: Observe runs per critical rebuild in the late batch.
        for (int i = 0; i < 100; i++) observer.Observe(i, i % 90, 5, HeightmapCriticalClause.Player, HeightmapRebuildContext.None, 0, true, true);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) observer.Observe(i, i % 90, 5, HeightmapCriticalClause.Camera, HeightmapRebuildContext.Teleporting, 20000, true, i % 2 == 0);
        for (int i = 0; i < 1000; i++) log.AgeMs(i, 9000, 1000, out _);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Observation must not allocate per rebuild.");
    }

    private static HeightmapRebuildCandidate[] Candidates() => new[]
    {
        new HeightmapRebuildCandidate { Id = 0, DistanceM = 0, ViewDot = 1, Critical = true },
        new HeightmapRebuildCandidate { Id = 1, DistanceM = 40, ViewDot = -1, Critical = true },
        new HeightmapRebuildCandidate { Id = 2, DistanceM = 150, ViewDot = 1, AgeMs = 100 },
        new HeightmapRebuildCandidate { Id = 3, DistanceM = 70, ViewDot = 0.2f, Critical = true },
        new HeightmapRebuildCandidate { Id = 4, DistanceM = 200, ViewDot = 1, AgeMs = 600 },
        new HeightmapRebuildCandidate { Id = 5, DistanceM = 300, ViewDot = -1 },
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
