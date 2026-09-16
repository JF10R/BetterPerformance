using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterPerformance.Core;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Read-only sampled queue diagnostics. No identifiers or per-object events
    // leave this module; observations end at native local object creation.
    internal static class LootQueueTelemetry
    {
        private const int Capacity = 1024, ScanLimit = 128;
        private const double ScanBudgetMs = 0.2, OverrunThresholdMs = 0.5, CooldownMs = 1000;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".LootQueueTelemetry");
        private static readonly LootQueueTracker<ZDOID> Tracker = new LootQueueTracker<ZDOID>(Capacity, 30000, 120000);
        private static readonly Dictionary<int, bool> PrefabKinds = new Dictionary<int, bool>(Capacity);
        private static ConfigEntry<bool> enabled = null!;
        private static ConfigEntry<int> scanInterval = null!;
        private static ConfigEntry<float> radius = null!;
        private static ManualLogSource logger = null!;
        // One bounded session reference permits cheap identity checks. Finish and
        // Reset release it; no game object or world is retained by CaptureSession.
        private static CaptureSession? capture;
        private static WeakReference<ZNetScene>? scene;
        private static string status = "disabled";
        private static bool observing;
        private static int cursor;
        private static double nextScan, cooldownUntil;
        private static long passes, partialPasses, scanned, nearby, opportunities, nonLootAhead, unknownPrefabs;
        private static long creationsUntracked;
        private static long overruns, cooldownSkips;
        private static int queueMax;
        private static double observationMs, observationMaxMs;

        internal static void ConfigureAndInstall(ConfigFile config, ManualLogSource log)
        {
            logger = log;
            enabled = config.Bind("Diagnostics", "LootQueueEnabled", true,
                "Sample nearby loot in the native creation queue while capturing. Read-only, bounded and independent of loot priority; reports local observed wait, not network latency.");
            scanInterval = config.Bind("Diagnostics", "LootQueueScanIntervalMilliseconds", 250,
                new ConfigDescription("Minimum spacing of queue scans. Short-lived loot can complete between scans and remain unobserved. Slow scans temporarily reduce coverage further.", new AcceptableValueRange<int>(33, 2000)));
            radius = config.Bind("Diagnostics", "LootQueueRadius", 20f,
                new ConfigDescription("Nearby loot observation radius in metres from the simulation reference position.", new AcceptableValueRange<float>(1f, 64f)));
            if (!enabled.Value) return;
            try
            {
                var sorted = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjectsSorted",
                    new[] { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() });
                var create = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
                if (sorted == null || sorted.ReturnType != typeof(void) || create == null || create.ReturnType != typeof(GameObject))
                    throw new InvalidOperationException("Unsupported object creation methods.");
                // Harmony executes this transpiler after our scheduler's. Inserting
                // immediately after Sort then places Observe before its priority call.
                var transpiler = new HarmonyMethod(typeof(LootQueueTelemetry), nameof(Transpile))
                    { after = new[] { Plugin.PluginId + ".ObjectCreationBudget" } };
                Patches.Patch(sorted, transpiler: transpiler);
                Patches.Patch(create, postfix: new HarmonyMethod(typeof(LootQueueTelemetry), nameof(Created)));
                status = "installed";
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                status = "unavailable";
                logger.LogWarning("Loot queue diagnostics unavailable: " + exception.GetType().Name);
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var sort = AccessTools.Method(typeof(List<ZDO>), "Sort", new[] { typeof(Comparison<ZDO>) });
            var calls = code.Where(instruction => instruction.Calls(sort)).ToList();
            var field = AccessTools.Field(typeof(ZNetScene), "m_tempCurrentObjects2");
            var create = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
            int createIndex = code.FindIndex(instruction => instruction.Calls(create));
            if (calls.Count != 1 || field == null || field.FieldType != typeof(List<ZDO>) ||
                code.IndexOf(calls[0]) >= createIndex || !code.Take(code.IndexOf(calls[0])).Any(instruction => instruction.LoadsField(field)))
                throw new InvalidOperationException("Unsupported native sorted queue layout.");
            foreach (var instruction in code)
            {
                yield return instruction;
                if (!ReferenceEquals(instruction, calls[0])) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, field);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LootQueueTelemetry), nameof(Observe)));
            }
        }

        private static void BindCapture(CaptureSession current)
        {
            if (!ReferenceEquals(capture, current))
            {
                Reset();
                capture = current;
            }
        }

        private static bool Active(CaptureSession current)
        {
            BindCapture(current);
            var currentScene = ZNetScene.instance;
            if (currentScene == null) return false;
            if (scene == null || !scene.TryGetTarget(out var previousScene) || !ReferenceEquals(previousScene, currentScene))
            {
                Tracker.Clear(true);
                PrefabKinds.Clear();
                scene = new WeakReference<ZNetScene>(currentScene);
                cursor = 0;
                nextScan = 0;
            }
            return true;
        }

        private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        private static void Observe(List<ZDO> candidates)
        {
            if (observing || status != "installed" || !enabled.Value) return;
            var current = System.Threading.Volatile.Read(ref TimingHooks.Current);
            if (current == null) return;
            double started = NowMs();
            // Capture start explicitly resets the deadline. Skipped calls avoid
            // scene lookups, weak references, dictionary scans and allocations.
            if (started < nextScan)
            {
                if (started < cooldownUntil) cooldownSkips++;
                return;
            }
            try
            {
                if (!Active(current)) return;
                observing = true;
                nextScan = started + Math.Max(33, Math.Min(2000, scanInterval.Value));
                Tracker.Expire(started, 32);
                passes++;
                queueMax = Math.Max(queueMax, candidates.Count);
                if (candidates.Count == 0) { cursor = 0; return; }
                float distance = radius.Value;
                if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 1 || distance > 64) return;
                float distanceSquared = distance * distance;
                int begin = cursor % candidates.Count, visited = 0, tier = int.MinValue, ahead = 0;
                for (int i = begin; i < candidates.Count && visited < ScanLimit; i++)
                {
                    if (NowMs() - started >= ScanBudgetMs) break;
                    var zdo = candidates[i];
                    visited++;
                    int currentTier = (int)zdo.Type;
                    if (tier != currentTier) { tier = currentTier; ahead = 0; }
                    // Distant candidates cannot be reprioritized by this feature.
                    // Unknown classifications are never counted as non-loot.
                    if (!(zdo.m_tempSortValue <= distanceSquared)) continue;
                    int hash = zdo.GetPrefab();
                    if (!PrefabKinds.TryGetValue(hash, out bool loot))
                    {
                        if (PrefabKinds.Count >= Capacity) { unknownPrefabs++; continue; }
                        var prefab = ZNetScene.instance.GetPrefab(hash);
                        if (prefab == null) { unknownPrefabs++; continue; }
                        loot = prefab.GetComponent<ItemDrop>() != null;
                        PrefabKinds.Add(hash, loot);
                    }
                    if (!loot) { ahead++; continue; }
                    nearby++;
                    if (ahead > 0) { opportunities++; nonLootAhead += ahead; }
                    Tracker.Observe(zdo.m_uid, NowMs());
                }
                scanned += visited;
                if (visited < candidates.Count) partialPasses++;
                // Continue through large queues without wrapping inside a scan;
                // sampled same-tier objects ahead are therefore a lower bound.
                cursor = begin + visited >= candidates.Count ? 0 : begin + Math.Max(1, visited);
            }
            catch (Exception exception) { Fail(exception); }
            finally
            {
                if (observing)
                {
                    double elapsed = Math.Max(0, NowMs() - started);
                    observationMs += elapsed;
                    observationMaxMs = Math.Max(observationMaxMs, elapsed);
                    // An individual prefab query cannot be preempted. Back off
                    // subsequent scans instead of repeatedly adding slow work.
                    if (elapsed > OverrunThresholdMs)
                    {
                        overruns++;
                        cooldownUntil = NowMs() + CooldownMs;
                        nextScan = Math.Max(nextScan, cooldownUntil);
                    }
                    observing = false;
                }
            }
        }

        private static void Created(ZDO __0, GameObject? __result)
        {
            if (observing || status != "installed" || !enabled.Value) return;
            var current = System.Threading.Volatile.Read(ref TimingHooks.Current);
            if (current == null || __result == null) return;
            try
            {
                BindCapture(current);
                // Most creations are not tracked loot. Preserve their aggregate
                // count without a clock read, scene lookup or weak-reference check.
                if (Tracker.Count == 0 || !Tracker.Contains(__0.m_uid))
                {
                    creationsUntracked++;
                    return;
                }
                if (!Active(current)) return;
                if (!Tracker.Complete(__0.m_uid, NowMs())) creationsUntracked++;
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void Fail(Exception exception)
        {
            status = "failed";
            Tracker.Clear(true);
            PrefabKinds.Clear();
            logger.LogWarning("Loot queue diagnostics disabled after observation failure: " + exception.GetType().Name);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            var summary = Tracker.Drain(NowMs());
            labels.Add(new TextValue("loot_queue_probe_status", status));
            labels.Add(new TextValue("loot_queue_probe_enabled", status == "installed" && enabled.Value ? "true" : "false"));
            labels.Add(new TextValue("loot_queue_wait_semantics", "sampled_local_first_observed_to_creation_lower_bound"));
            labels.Add(new TextValue("loot_queue_coverage", "partial_rotating_sample; cadence_and_overrun_cooldown_can_miss_short_waits"));
            gauges.Add(new NumberValue("loot_queue_scan_interval", scanInterval.Value, "ms"));
            gauges.Add(new NumberValue("loot_queue_scan_candidate_limit", ScanLimit, "objects"));
            gauges.Add(new NumberValue("loot_queue_scan_soft_budget", ScanBudgetMs, "ms"));
            gauges.Add(new NumberValue("loot_queue_scan_overrun_threshold", OverrunThresholdMs, "ms"));
            gauges.Add(new NumberValue("loot_queue_scan_cooldown", CooldownMs, "ms"));
            gauges.Add(new NumberValue("loot_queue_scan_overruns", overruns, "scans"));
            gauges.Add(new NumberValue("loot_queue_cooldown_skips", cooldownSkips, "calls"));
            gauges.Add(new NumberValue("loot_queue_radius", radius.Value, "metres"));
            gauges.Add(new NumberValue("loot_queue_scan_passes", passes, "passes"));
            gauges.Add(new NumberValue("loot_queue_partial_passes", partialPasses, "passes"));
            gauges.Add(new NumberValue("loot_queue_candidates_max", queueMax, "objects"));
            gauges.Add(new NumberValue("loot_queue_candidates_scanned", scanned, "observations"));
            gauges.Add(new NumberValue("loot_queue_nearby_loot_observations", nearby, "observations"));
            gauges.Add(new NumberValue("loot_queue_priority_opportunities", opportunities, "observations"));
            gauges.Add(new NumberValue("loot_queue_same_tier_nonloot_ahead_sum", nonLootAhead, "sampled_objects"));
            gauges.Add(new NumberValue("loot_queue_unknown_prefabs", unknownPrefabs, "observations"));
            gauges.Add(new NumberValue("loot_queue_tracks_started", summary.Observed, "tracks"));
            gauges.Add(new NumberValue("loot_queue_creations_observed", summary.Completed, "objects"));
            gauges.Add(new NumberValue("loot_queue_tracks_censored", summary.Censored, "tracks"));
            gauges.Add(new NumberValue("loot_queue_track_capacity_skips", summary.CapacitySkipped, "observations"));
            gauges.Add(new NumberValue("loot_queue_wait_sum", summary.CompletedSumMs, "ms"));
            gauges.Add(new NumberValue("loot_queue_wait_max", summary.CompletedMaxMs, "ms"));
            gauges.Add(new NumberValue("loot_queue_pending", summary.Pending, "tracks"));
            gauges.Add(new NumberValue("loot_queue_pending_age_max", summary.PendingMaxAgeMs, "ms"));
            gauges.Add(new NumberValue("loot_queue_untracked_creations", creationsUntracked, "all_objects"));
            gauges.Add(new NumberValue("loot_queue_scan_elapsed_sum", observationMs, "ms"));
            gauges.Add(new NumberValue("loot_queue_scan_elapsed_max", observationMaxMs, "ms"));
            ClearInterval();
        }

        // Call after detaching TimingHooks.Current and before exporting the final
        // record. Pending tracks are censored, not successful zero-length waits.
        internal static void Finish(List<NumberValue> gauges, List<TextValue> labels)
        {
            Tracker.Clear(true);
            Sample(gauges, labels);
            capture = null;
        }

        private static void ClearInterval()
        {
            passes = partialPasses = scanned = nearby = opportunities = nonLootAhead = unknownPrefabs = creationsUntracked = 0;
            overruns = cooldownSkips = 0;
            queueMax = 0;
            observationMs = observationMaxMs = 0;
        }

        internal static void Reset()
        {
            Tracker.Clear(false);
            PrefabKinds.Clear();
            capture = null;
            scene = null;
            cursor = 0;
            nextScan = cooldownUntil = 0;
            ClearInterval();
        }

        internal static void Uninstall() { Patches.UnpatchSelf(); Reset(); status = "disabled"; }
    }
}
