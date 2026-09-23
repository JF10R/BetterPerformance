using System;

namespace BetterPerformance.Core
{
    public enum HeightmapRebuildReason : byte
    {
        // Left queued for a later frame.
        Deferred,
        // Near the player or inside the grass radius around the camera.
        Critical,
        // Deferred for at least the configured maximum.
        Overdue,
        // Chosen by priority inside this frame's allowance.
        Budget,
    }

    // One heightmap whose rebuild vanilla queued for this frame's late update.
    public struct HeightmapRebuildCandidate
    {
        public int Id;
        public float DistanceM; // camera to the nearest point of the heightmap square, XZ
        public float ViewDot;   // camera forward . direction to the heightmap centre, XZ
        public double AgeMs;    // since the rebuild was first seen queued; 0 when new
        public bool Critical;
        public HeightmapRebuildReason Reason; // output
        public bool Run => Reason != HeightmapRebuildReason.Deferred;
    }

    public struct HeightmapRebuildSettings
    {
        public double BudgetMs, CostEstimateMs, MaxDeferMs, FrameMs;
    }

    public struct HeightmapRebuildPlan
    {
        public int Critical, Overdue, Budgeted, Deferred;
        public double AllowanceMs;
    }

    // Decides which queued rebuilds run this frame. Pure and allocation-free: the caller owns
    // the candidate and order arrays. Critical and overdue always run; the rest run nearest
    // and in-view first while the projected spend fits the allowance, at least one per frame.
    public static class HeightmapRebuildPlanner
    {
        // cos 60 degrees: the default 16:9 view is about 50 degrees either side of forward.
        public const float InViewCos = 0.5f;
        public const float OffViewWeight = 1.3f, BehindWeight = 2f;

        // Effective distance; lower runs first. Behind the camera weighs double: a rebuild
        // there is the least likely to be seen before its deadline forces it anyway.
        public static float Priority(float distanceM, float viewDot)
        {
            if (float.IsNaN(distanceM) || float.IsNaN(viewDot)) return float.PositiveInfinity;
            float distance = distanceM < 0 ? 0 : distanceM;
            return viewDot >= InViewCos ? distance : viewDot >= 0 ? distance * OffViewWeight : distance * BehindWeight;
        }

        public static HeightmapRebuildPlan Plan(HeightmapRebuildCandidate[] candidates, int count, int[] order,
            HeightmapRebuildSettings settings)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (order == null) throw new ArgumentNullException(nameof(order));
            if (count < 0 || count > candidates.Length || count > order.Length) throw new ArgumentOutOfRangeException(nameof(count));
            double cost = Positive(settings.CostEstimateMs, 1);
            double frame = Positive(settings.FrameMs, 16);
            double budget = Positive(settings.BudgetMs, 0);
            double maxDefer = Positive(settings.MaxDeferMs, 0);
            var plan = new HeightmapRebuildPlan();
            int optional = 0;
            double oldest = 0;
            for (int i = 0; i < count; i++)
            {
                ref HeightmapRebuildCandidate candidate = ref candidates[i];
                double age = double.IsNaN(candidate.AgeMs) || candidate.AgeMs < 0 ? 0 : candidate.AgeMs;
                if (candidate.Critical) { candidate.Reason = HeightmapRebuildReason.Critical; plan.Critical++; }
                else if (age >= maxDefer) { candidate.Reason = HeightmapRebuildReason.Overdue; plan.Overdue++; }
                else
                {
                    candidate.Reason = HeightmapRebuildReason.Deferred;
                    order[optional++] = i;
                    if (age > oldest) oldest = age;
                }
            }
            // Pace the optional work so it drains before the oldest one turns overdue;
            // otherwise a burst would only move, whole, to the frame its deadline lands on.
            double framesLeft = Math.Max(1, Math.Floor((maxDefer - oldest) / frame));
            double spent = (plan.Critical + plan.Overdue) * cost;
            plan.AllowanceMs = Math.Max(budget, spent + optional * cost / framesLeft);
            SortByPriority(candidates, order, optional);
            for (int k = 0; k < optional; k++)
            {
                if (k == 0 || spent + cost <= plan.AllowanceMs)
                {
                    candidates[order[k]].Reason = HeightmapRebuildReason.Budget;
                    spent += cost;
                    plan.Budgeted++;
                }
                else plan.Deferred++;
            }
            return plan;
        }

        // Stable insertion sort: queues are tens of entries and ties keep the native order.
        private static void SortByPriority(HeightmapRebuildCandidate[] candidates, int[] order, int count)
        {
            for (int i = 1; i < count; i++)
            {
                int item = order[i];
                float key = Priority(candidates[item].DistanceM, candidates[item].ViewDot);
                int j = i - 1;
                while (j >= 0 && Priority(candidates[order[j]].DistanceM, candidates[order[j]].ViewDot) > key)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = item;
            }
        }

        private static double Positive(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;
    }
}
