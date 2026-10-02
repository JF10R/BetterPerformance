using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterPerformance
{
    // Heightmap.CustomLateUpdate runs, in one frame, every rebuild that TerrainModifier queued
    // with Poke(2) — dozens when a zone with locations loads or unloads. This module skips that
    // call for some queued heightmaps so they stay queued and vanilla runs them on a later frame:
    // near the player and camera always, overdue always, the rest nearest and in view first
    // under a per-frame allowance. Client only; docs/heightmap-rebuild-budget.md.
    internal static class HeightmapRebuildBudget
    {
        // Heightmap.m_doLateUpdate value CustomLateUpdate consumes (Poke(2)).
        private const int QueuedLate = 2;
        private const double InitialCostMs = 5; // 2026-09-23 captures: 4-8 ms per rebuild
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".HeightmapRebuildBudget");
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<float>? budgetMs, criticalRadius, maxDeferMs;
        // Keyed by instance id, rebuilt at every plan from the heightmaps still queued, so a
        // heightmap that ran, was flushed by ForceGenerateAll or was destroyed drops out.
        private static Dictionary<int, Entry> entries = new Dictionary<int, Entry>(), spare = new Dictionary<int, Entry>();
        private static HeightmapRebuildCandidate[] candidates = new HeightmapRebuildCandidate[64];
        private static long[] firstSeen = new long[64];
        private static int[] order = new int[64];
        private static int plannedFrame = -1, optionalRan;
        private static double allowanceMs = double.MaxValue, costEstimateMs = InitialCostMs;
        // This frame's measured spend; its last frame is folded at the next plan or sample.
        private static readonly HeightmapFrameSpend Spend = new HeightmapFrameSpend();
        private static long rebuildsRun, deferred, demoted, overdue, critical, budgeted, plannedFrames, failures;
        private static double maxDeferralMs, planMsMax, acrossReloadMaxMs;
        private static long acrossReload;
        private static int queuePeak;
        // Observer of the critical rebuilds (where, cost, context); read-only beside the plan.
        private static readonly HeightmapCriticalProfile CriticalProfile = new HeightmapCriticalProfile();
        private static readonly HeightmapEnableLog Enables = new HeightmapEnableLog();
        private static float[] gaps = new float[64];
        private static HeightmapCriticalClause[] clauses = new HeightmapCriticalClause[64];
        private static HeightmapRebuildContext frameContext;
        private static bool framePlanned, enableLogInstalled;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        private struct Entry
        {
            internal long First;
            internal int Frame;
            internal bool Run, Optional, Ran;
            internal bool Critical;
            internal float PlayerGapM;
            internal HeightmapCriticalClause Clause;
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Terrain", "RebuildBudgetEnabled", false,
                "Experimental. Spread queued terrain rebuilds (Heightmap.CustomLateUpdate) across frames, nearest and in view first. " +
                "Rebuilds near the player and camera and overdue ones always run; nothing is dropped. Client only. Requires restart.");
            budgetMs = config.Bind("Terrain", "RebuildBudgetMilliseconds", 4f, new ConfigDescription(
                "Soft per-frame allowance for queued rebuilds away from the player. At least one runs per frame, and the allowance " +
                "rises when needed to drain the queue before RebuildMaxDeferMilliseconds.",
                new AcceptableValueRange<float>(1f, 50f)));
            criticalRadius = config.Bind("Terrain", "RebuildCriticalRadius", 80f, new ConfigDescription(
                "Metres from the player (and camera) inside which a queued rebuild is never deferred. 80 covers the native creature " +
                "spawn ring (40-80 m), which reads the terrain collider.",
                new AcceptableValueRange<float>(32f, 512f)));
            maxDeferMs = config.Bind("Terrain", "RebuildMaxDeferMilliseconds", 500f, new ConfigDescription(
                "Longest a queued rebuild may wait. Past it, it runs whatever the allowance.",
                new AcceptableValueRange<float>(50f, 5000f)));
            if (!option.Value) { Status = "disabled"; return; }
            // A dedicated server is headless; its terrain feeds physics for every peer.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) { Status = "not_applicable_headless"; return; }
            MethodInfo lateUpdate;
            try { lateUpdate = ValidateContracts(); }
            catch (Exception exception)
            {
                Status = "unavailable";
                logger.LogWarning("Heightmap rebuild budget unavailable; native rebuilds retained: " +
                    exception.GetType().Name + ": " + exception.Message);
                return;
            }
            try
            {
                Patches.Patch(lateUpdate, prefix: new HarmonyMethod(typeof(HeightmapRebuildBudget), nameof(BeforeLateUpdate)),
                    finalizer: new HarmonyMethod(typeof(HeightmapRebuildBudget), nameof(AfterLateUpdate)));
                Installed = Enabled = true;
                Status = "installed";
                logger.LogWarning("Experimental heightmap rebuild budget installed at " + budgetMs.Value + " ms per frame, critical radius " +
                    criticalRadius.Value + " m, max deferral " + maxDeferMs.Value + " ms.");
            }
            catch (Exception exception)
            {
                try { PatchRemoval.UnpatchOwned(Patches); } catch { failures++; }
                Installed = Enabled = false;
                Status = "patch_failed";
                logger.LogWarning("Heightmap rebuild budget could not patch Heightmap.CustomLateUpdate: " + exception.GetType().Name);
                return;
            }
            // Observation only, separate: losing it costs the enable-age gauges, never the budget.
            try
            {
                Type heightmap = lateUpdate.DeclaringType;
                MethodInfo enable = AccessTools.DeclaredMethod(heightmap, "OnEnable", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("Heightmap.OnEnable is missing.");
                Patches.Patch(enable, postfix: new HarmonyMethod(typeof(HeightmapRebuildBudget), nameof(AfterEnable)));
                enableLogInstalled = true;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Heightmap rebuild budget: enable-age observation unavailable: " + exception.GetType().Name);
            }
        }

        private static void AfterEnable(MonoBehaviour __instance)
        {
            try { Enables.Note(__instance.GetInstanceID(), Stopwatch.GetTimestamp()); }
            catch { failures++; }
        }

        // Every member the hooks read is resolved here, so an unsupported layout leaves the
        // native late update alone instead of failing inside it. Heightmap cannot be named in
        // a signature (standalone CLR type-load limit), hence the by-name lookups.
        private static MethodInfo ValidateContracts()
        {
            Type heightmap = typeof(ZNet).Assembly.GetType("Heightmap", false)
                ?? throw new InvalidOperationException("Heightmap is missing.");
            MethodInfo lateUpdate = AccessTools.DeclaredMethod(heightmap, "CustomLateUpdate", new[] { typeof(float) })
                ?? throw new InvalidOperationException("Heightmap.CustomLateUpdate(float) is missing.");
            if (lateUpdate.IsStatic || lateUpdate.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported Heightmap.CustomLateUpdate signature.");
            FieldInfo? state = AccessTools.DeclaredField(heightmap, "m_doLateUpdate");
            if (state == null || state.IsStatic || state.FieldType != typeof(int))
                throw new InvalidOperationException("Heightmap.m_doLateUpdate is not an instance int.");
            if (AccessTools.DeclaredProperty(heightmap, "Instances")?.GetGetMethod() is not MethodInfo instances || !instances.IsStatic)
                throw new InvalidOperationException("Heightmap.Instances is missing.");
            if (AccessTools.DeclaredMethod(heightmap, "IsPointInside", new[] { typeof(Vector3), typeof(float) })?.ReturnType != typeof(bool))
                throw new InvalidOperationException("Heightmap.IsPointInside(Vector3, float) is missing.");
            if (AccessTools.DeclaredField(heightmap, "m_width")?.FieldType != typeof(int) ||
                AccessTools.DeclaredField(heightmap, "m_scale")?.FieldType != typeof(float))
                throw new InvalidOperationException("Unsupported Heightmap size fields.");
            if (AccessTools.DeclaredProperty(heightmap, "IsDistantLod")?.PropertyType != typeof(bool))
                throw new InvalidOperationException("Heightmap.IsDistantLod is missing.");
            if (AccessTools.DeclaredField(typeof(ClutterSystem), "m_distance")?.FieldType != typeof(float))
                throw new InvalidOperationException("ClutterSystem.m_distance is missing.");
            return lateUpdate;
        }

        // Returning false leaves m_doLateUpdate at 2: the rebuild stays queued, visible to
        // ForceGenerateAll and ClutterSystem exactly as vanilla sees it, and runs next frame.
        private static bool BeforeLateUpdate(MonoBehaviour __instance, out long __state)
        {
            __state = 0;
            if (!Enabled) return true;
            try
            {
                var map = (Heightmap)__instance;
                if (map.m_doLateUpdate != QueuedLate) return true;
                int frame = Time.frameCount;
                if (frame != plannedFrame) PlanFrame(frame);
                if (entries.TryGetValue(map.GetInstanceID(), out Entry entry) && entry.Frame == frame)
                {
                    if (!entry.Run) return false;
                    if (entry.Optional)
                    {
                        // The plan used an estimate; the measured spend has the last word, but
                        // never over the one optional rebuild that keeps the queue moving.
                        if (optionalRan > 0 && Spend.Ms >= allowanceMs)
                        {
                            entry.Run = false;
                            entries[map.GetInstanceID()] = entry;
                            deferred++;
                            demoted++;
                            return false;
                        }
                        optionalRan++;
                    }
                }
                __state = Stopwatch.GetTimestamp();
            }
            catch { failures++; __state = 0; }
            return true;
        }

        private static void AfterLateUpdate(MonoBehaviour __instance, long __state)
        {
            if (__state == 0) return;
            try
            {
                double elapsed = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                rebuildsRun++;
                Spend.Add(elapsed);
                if (elapsed >= 0 && elapsed < 1000) costEstimateMs = Math.Max(0.1, costEstimateMs + 0.2 * (elapsed - costEstimateMs));
                int id = __instance.GetInstanceID();
                if (!entries.TryGetValue(id, out Entry entry)) return;
                entry.Ran = true;
                entries[id] = entry;
                if (!entry.Critical || entry.Frame != plannedFrame) return;
                long now = Stopwatch.GetTimestamp();
                bool exact = false;
                double age = enableLogInstalled ? Enables.AgeMs(id, now, Stopwatch.Frequency, out exact) : double.NaN;
                CriticalProfile.Observe(plannedFrame, entry.PlayerGapM, elapsed, entry.Clause, frameContext, age, exact, framePlanned);
            }
            catch { failures++; }
        }

        private static void PlanFrame(int frame)
        {
            Spend.Open(frame, budgetMs?.Value ?? 4f);
            plannedFrame = frame;
            optionalRan = 0;
            allowanceMs = double.MaxValue;
            framePlanned = false;
            long started = Stopwatch.GetTimestamp();
            spare.Clear();
            Camera camera = Utils.GetMainCamera();
            ZNet network = ZNet.instance;
            if (camera == null || network == null || network.IsDedicated()) { Swap(); return; }
            Transform view = camera.transform;
            Vector3 eye = view.position;
            Vector3 forward = view.forward;
            forward.y = 0;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            Player player = Player.m_localPlayer;
            Vector3 anchor = player != null ? player.transform.position : eye;
            float radius = criticalRadius?.Value ?? 80f;
            // ClutterSystem withholds grass while any heightmap within m_distance of the camera
            // is queued; those stay critical so a deferral never stalls grass.
            ClutterSystem clutter = ClutterSystem.instance;
            float eyeRadius = clutter != null ? Math.Max(radius, clutter.m_distance + 1f) : radius;
            // Observation only; it must never abort the plan.
            try
            {
                frameContext = TeleportLoadingTelemetry.Teleporting ? HeightmapRebuildContext.Teleporting
                    : TeleportLoadingTelemetry.AfterArrival ? HeightmapRebuildContext.AfterArrival : HeightmapRebuildContext.None;
            }
            catch { frameContext = HeightmapRebuildContext.None; }
            List<IMonoUpdater> all = Heightmap.Instances;
            int count = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (!(all[i] is Heightmap map) || map == null || map.m_doLateUpdate != QueuedLate) continue;
                int id = map.GetInstanceID();
                if (spare.ContainsKey(id)) continue;
                if (count == candidates.Length) Grow();
                long first = entries.TryGetValue(id, out Entry previous) && !previous.Ran ? previous.First : started;
                Vector3 centre = map.transform.position;
                float half = map.m_width * map.m_scale * 0.5f;
                float dx = Math.Max(0, Math.Abs(centre.x - eye.x) - half), dz = Math.Max(0, Math.Abs(centre.z - eye.z) - half);
                Vector3 toward = new Vector3(centre.x - eye.x, 0, centre.z - eye.z);
                float length = toward.magnitude;
                candidates[count] = new HeightmapRebuildCandidate
                {
                    Id = id,
                    DistanceM = (float)Math.Sqrt(dx * dx + dz * dz),
                    ViewDot = length > 1e-3f ? Vector3.Dot(forward, toward / length) : 1f,
                    AgeMs = (started - first) * 1000.0 / Stopwatch.Frequency,
                    // Distant-LOD heightmaps are never queued natively; if one is, it is not ours to defer.
                    Critical = map.IsDistantLod || map.IsPointInside(anchor, radius) || map.IsPointInside(eye, eyeRadius),
                };
                firstSeen[count] = first;
                // Observation only, after the decision above: which clause held, and the player's
                // distance to the square in the test's own metric (per-axis gap, 0 on the square).
                bool isCritical = candidates[count].Critical;
                clauses[count] = !isCritical || map.IsDistantLod ? HeightmapCriticalClause.DistantLod // unread unless critical
                    : map.IsPointInside(anchor, radius) ? HeightmapCriticalClause.Player : HeightmapCriticalClause.Camera;
                gaps[count] = Math.Max(Math.Max(0, Math.Abs(centre.x - anchor.x) - half), Math.Max(0, Math.Abs(centre.z - anchor.z) - half));
                spare[id] = new Entry { First = first, Frame = frame, Run = true, Critical = isCritical, PlayerGapM = gaps[count], Clause = clauses[count] };
                count++;
            }
            Swap();
            if (count > queuePeak) queuePeak = count;
            if (count < 2) return; // Nothing to order: vanilla runs it.
            framePlanned = true;
            var plan = HeightmapRebuildPlanner.Plan(candidates, count, order, new HeightmapRebuildSettings
            {
                BudgetMs = budgetMs?.Value ?? 4f,
                CostEstimateMs = costEstimateMs,
                MaxDeferMs = maxDeferMs?.Value ?? 500f,
                FrameMs = Math.Min(100, Math.Max(1, Time.unscaledDeltaTime * 1000.0)),
            });
            allowanceMs = plan.AllowanceMs;
            for (int i = 0; i < count; i++)
            {
                ref HeightmapRebuildCandidate candidate = ref candidates[i];
                entries[candidate.Id] = new Entry
                {
                    First = firstSeen[i],
                    Frame = frame,
                    Run = candidate.Run,
                    Optional = candidate.Reason == HeightmapRebuildReason.Budget,
                    Critical = candidate.Critical,
                    PlayerGapM = gaps[i],
                    Clause = clauses[i],
                };
                if (candidate.Run && candidate.AgeMs > maxDeferralMs) RecordDeferral(candidate.Id, candidate.AgeMs, started);
            }
            critical += plan.Critical;
            overdue += plan.Overdue;
            budgeted += plan.Budgeted;
            deferred += plan.Deferred;
            plannedFrames++;
            double planMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (planMs > planMsMax) planMsMax = planMs;
        }

        // Observation only. A heightmap queued, then unloaded with its zone and enabled again keeps its first-seen
        // time, so its age spans the unload (2026-09-30: 33.8 s and 13.3 s, both forced at once on return).
        // Those ages are reported apart instead of as a deferral.
        private static void RecordDeferral(int id, double ageMs, long now)
        {
            double enabledMs = double.NaN;
            bool exact = false;
            if (enableLogInstalled) enabledMs = Enables.AgeMs(id, now, Stopwatch.Frequency, out exact);
            if (exact && enabledMs < ageMs)
            {
                acrossReload++;
                if (ageMs > acrossReloadMaxMs) acrossReloadMaxMs = ageMs;
                return;
            }
            maxDeferralMs = ageMs;
        }

        private static void Swap()
        {
            var previous = entries;
            entries = spare;
            spare = previous;
            spare.Clear();
        }

        private static void Grow()
        {
            Array.Resize(ref candidates, candidates.Length * 2);
            Array.Resize(ref firstSeen, firstSeen.Length * 2);
            Array.Resize(ref order, order.Length * 2);
            Array.Resize(ref gaps, gaps.Length * 2);
            Array.Resize(ref clauses, clauses.Length * 2);
        }

        // Main-thread runtime toggle. Off, every queued rebuild runs on its next late update.
        internal static bool SetEnabled(bool enabled)
        {
            Enabled = Installed && enabled;
            return Enabled;
        }

        // The capture's final export: it can run after this frame's late batch (plugin shutdown), so the
        // frame still marked current is complete too and is folded before sampling.
        internal static void Finish(List<NumberValue> gauges, List<TextValue> labels)
        {
            Spend.Close(budgetMs?.Value ?? 4f);
            Sample(gauges, labels);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            // Sampling runs in Update, never inside the late batch: the burst's last frame is complete.
            if (Spend.Pending) Spend.CloseBefore(CurrentFrame(), budgetMs?.Value ?? 4f);
            gauges.Add(new NumberValue("heightmap_budget_rebuilds_run", Take(ref rebuildsRun), "rebuilds"));
            gauges.Add(new NumberValue("heightmap_budget_deferred", Take(ref deferred), "decisions"));
            gauges.Add(new NumberValue("heightmap_budget_demoted_measured", Take(ref demoted), "decisions"));
            gauges.Add(new NumberValue("heightmap_budget_overdue_forced", Take(ref overdue), "rebuilds"));
            gauges.Add(new NumberValue("heightmap_budget_critical", Take(ref critical), "rebuilds"));
            gauges.Add(new NumberValue("heightmap_budget_budgeted", Take(ref budgeted), "rebuilds"));
            gauges.Add(new NumberValue("heightmap_budget_planned_frames", Take(ref plannedFrames), "frames"));
            gauges.Add(new NumberValue("heightmap_budget_frames_over_budget", Spend.TakeFramesOverBudget(), "frames"));
            gauges.Add(new NumberValue("heightmap_budget_max_deferral_ms", Math.Round(maxDeferralMs, 3), "ms"));
            gauges.Add(new NumberValue("heightmap_budget_age_across_reload", Take(ref acrossReload), "rebuilds"));
            gauges.Add(new NumberValue("heightmap_budget_age_across_reload_max_ms", Math.Round(acrossReloadMaxMs, 3), "ms"));
            gauges.Add(new NumberValue("heightmap_budget_frame_spend_max_ms", Math.Round(Spend.TakeMaxMs(), 3), "ms"));
            gauges.Add(new NumberValue("heightmap_budget_plan_ms_max", Math.Round(planMsMax, 3), "ms"));
            gauges.Add(new NumberValue("heightmap_budget_queue_peak", queuePeak, "heightmaps"));
            gauges.Add(new NumberValue("heightmap_budget_cost_estimate_ms", Math.Round(costEstimateMs, 3), "ms"));
            gauges.Add(new NumberValue("heightmap_budget_probe_failures", Take(ref failures), "calls"));
            maxDeferralMs = planMsMax = acrossReloadMaxMs = 0;
            queuePeak = 0;
            CriticalProfile.Sample(gauges);
            labels.Add(new TextValue("heightmap_budget_status", Status));
            labels.Add(new TextValue("heightmap_budget_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("heightmap_budget_ms", (budgetMs?.Value ?? 0f).ToString(CultureInfo.InvariantCulture)));
            labels.Add(new TextValue("heightmap_budget_critical_observer",
                !Installed ? "not_installed" : enableLogInstalled ? "installed" : "installed_without_enable_age"));
        }

        // Separate method: Unity's Time cannot load in the contract harness, which still calls Sample.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CurrentFrame() => Time.frameCount;

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset()
        {
            rebuildsRun = deferred = demoted = overdue = critical = budgeted = plannedFrames = failures = acrossReload = 0;
            maxDeferralMs = planMsMax = acrossReloadMaxMs = 0;
            queuePeak = 0;
            // Runs from StartCapture in Update, outside the late batch: the spend is never read
            // again before the next plan zeroes it, so dropping it here changes no decision.
            Spend.Reset();
            CriticalProfile.Reset();
        }

        internal static void Uninstall()
        {
            Installed = Enabled = false;
            Status = "disabled";
            enableLogInstalled = false;
            Enables.Clear();
            entries.Clear();
            spare.Clear();
            plannedFrame = -1;
            costEstimateMs = InitialCostMs;
            Reset();
            try { PatchRemoval.UnpatchOwned(Patches); } catch { }
            option = null;
            budgetMs = criticalRadius = maxDeferMs = null;
        }
    }
}
