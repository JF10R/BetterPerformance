using System;
using System.Linq;
using BetterPerformance.Core;

internal static class HeightmapRebuildPlannerTests
{
    private static readonly HeightmapRebuildSettings Defaults = new HeightmapRebuildSettings
    { BudgetMs = 4, CostEstimateMs = 2, MaxDeferMs = 500, FrameMs = 16 };

    public static void Run()
    {
        // Priority: in view first, then the side, then behind, each scaled by distance.
        Check(HeightmapRebuildPlanner.Priority(150, 0.9f) < HeightmapRebuildPlanner.Priority(120, 0.2f) &&
            HeightmapRebuildPlanner.Priority(120, 0.2f) < HeightmapRebuildPlanner.Priority(100, -0.5f),
            "150 m in view beats 120 m to the side (156) beats 100 m behind (200).");
        Check(HeightmapRebuildPlanner.Priority(100, 0.5f) == 100 && Math.Abs(HeightmapRebuildPlanner.Priority(100, 0f) - 130f) < 1e-3 &&
            HeightmapRebuildPlanner.Priority(100, -0.01f) == 200, "Cone and hemisphere boundaries are inclusive on the near side.");
        Check(float.IsPositiveInfinity(HeightmapRebuildPlanner.Priority(float.NaN, 1)) &&
            HeightmapRebuildPlanner.Priority(-5, 1) == 0, "Invalid distances sort last; negative clamps to zero.");

        // Within budget: est 2 ms, budget 4 ms -> two optional run, the rest wait, nearest first.
        var c = Candidates((300, 1, 0, false), (100, 1, 0, false), (200, 1, 0, false), (50, -1, 0, false));
        var order = new int[c.Length];
        var plan = HeightmapRebuildPlanner.Plan(c, c.Length, order, Defaults);
        Check(plan.Budgeted == 2 && plan.Deferred == 2 && plan.Critical == 0 && plan.Overdue == 0 && plan.AllowanceMs == 4,
            "Two 2 ms rebuilds fit a 4 ms allowance; the other two wait.");
        Check(c[1].Run && c[3].Run && !c[0].Run && !c[2].Run,
            "100 m in view and 50 m behind (100 effective) run; 200 m and 300 m wait.");

        // Critical and overdue always run and consume the allowance first; one optional still runs.
        c = Candidates((10, 1, 0, true), (20, 1, 0, true), (30, 1, 900, false), (40, 1, 0, false), (60, 1, 0, false));
        plan = HeightmapRebuildPlanner.Plan(c, c.Length, new int[c.Length], Defaults);
        Check(c[0].Reason == HeightmapRebuildReason.Critical && c[1].Reason == HeightmapRebuildReason.Critical &&
            c[2].Reason == HeightmapRebuildReason.Overdue, "Critical and overdue are never deferred.");
        Check(c[3].Reason == HeightmapRebuildReason.Budget && !c[4].Run && plan.Budgeted == 1 && plan.Deferred == 1,
            "Mandatory work over budget still lets exactly one optional rebuild progress.");
        Check(Math.Abs(plan.AllowanceMs - (6 + 2 * 2 / 31.0)) < 1e-9,
            "Allowance is mandatory spend plus the paced share when that exceeds the budget.");

        // Pacing: 9 optional 5 ms rebuilds, oldest 452 ms of a 500 ms limit at 16 ms frames:
        // three frames left, so 15 ms this frame instead of a 45 ms spike three frames later.
        c = Enumerable.Range(0, 9).Select(i => new HeightmapRebuildCandidate
            { Id = i, DistanceM = 100 + i, ViewDot = 1, AgeMs = i == 0 ? 452 : 0 }).ToArray();
        order = new int[c.Length];
        plan = HeightmapRebuildPlanner.Plan(c, c.Length, order,
            new HeightmapRebuildSettings { BudgetMs = 4, CostEstimateMs = 5, MaxDeferMs = 500, FrameMs = 16 });
        Check(plan.AllowanceMs == 15 && plan.Budgeted == 3 && plan.Deferred == 6 && c[0].Run && c[1].Run && c[2].Run,
            "Deadline pacing raises the allowance to spread the queue before it turns overdue.");

        // A single rebuild costlier than the budget still runs; ties keep native order.
        c = Candidates((80, 1, 0, false), (80, 1, 0, false));
        plan = HeightmapRebuildPlanner.Plan(c, c.Length, new int[2],
            new HeightmapRebuildSettings { BudgetMs = 1, CostEstimateMs = 8, MaxDeferMs = 500, FrameMs = 16 });
        Check(c[0].Run && !c[1].Run && plan.Budgeted == 1, "Equal priority keeps the native order; one runs regardless of cost.");

        // Invalid settings fail open to vanilla: a zero or NaN defer limit makes every rebuild overdue.
        c = Candidates((80, 1, 0, false), (90, 1, 0, false));
        plan = HeightmapRebuildPlanner.Plan(c, c.Length, new int[2],
            new HeightmapRebuildSettings { BudgetMs = double.NaN, CostEstimateMs = double.NaN, MaxDeferMs = double.NaN, FrameMs = 0 });
        Check(plan.Overdue == 2 && c.All(x => x.Run), "Invalid settings never defer.");
        plan = HeightmapRebuildPlanner.Plan(c, 0, new int[0], Defaults);
        Check(plan.Budgeted + plan.Deferred + plan.Critical + plan.Overdue == 0 && plan.AllowanceMs == 4, "An empty queue plans nothing.");
        c = Candidates((80, 1, double.NaN, false), (90, 1, -3, false), (float.NaN, 1, 0, false));
        HeightmapRebuildPlanner.Plan(c, c.Length, new int[3], Defaults);
        Check(c[0].Run && c[1].Run && !c[2].Run, "NaN/negative ages count as new; a NaN distance sorts last.");
        bool rejected = false;
        try { HeightmapRebuildPlanner.Plan(c, 4, new int[4], Defaults); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "A count beyond the candidate array is rejected.");

        // Allocation-free once warm: the plugin calls this every frame with a queue.
        var warm = Enumerable.Range(0, 64).Select(i => new HeightmapRebuildCandidate
            { Id = i, DistanceM = 64 - i, ViewDot = i % 3 - 1, AgeMs = i }).ToArray();
        var warmOrder = new int[64];
        for (int i = 0; i < 100; i++) HeightmapRebuildPlanner.Plan(warm, warm.Length, warmOrder, Defaults);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) HeightmapRebuildPlanner.Plan(warm, warm.Length, warmOrder, Defaults);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Planning must not allocate per frame.");
    }

    private static HeightmapRebuildCandidate[] Candidates(params (float Distance, float Dot, double Age, bool Critical)[] items) =>
        items.Select((item, i) => new HeightmapRebuildCandidate
        { Id = i, DistanceM = item.Distance, ViewDot = item.Dot, AgeMs = item.Age, Critical = item.Critical }).ToArray();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
