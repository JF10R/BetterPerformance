using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BetterPerformance.Core;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // The reason names report observed evidence. In the verified vanilla layout,
    // zone-loaded + area-false means a valid prefab instance has not been created.
    internal enum LoadingWaitReason { Ready, MinimumDelayConsistent, ZoneNotLoaded, AreaNotReadyWithZoneLoaded, AreaNotReadyUnknown, FallbackOrOther, Exception }
    internal struct LoadingWaitEvidence
    {
        internal bool Logout, Custom, AfterDeath, AreaSeen, AreaReady;
        internal int Zone; // 0 unavailable, 1 observed true, 2 observed false.
        internal float WaitAtComparison, Minimum;
        internal LoadingWaitReason Classify(bool ready, bool failed)
        {
            if (failed) return LoadingWaitReason.Exception;
            if (ready) return LoadingWaitReason.Ready;
            if (AreaSeen && !AreaReady) return Zone == 2 ? LoadingWaitReason.ZoneNotLoaded :
                Zone == 1 ? LoadingWaitReason.AreaNotReadyWithZoneLoaded : LoadingWaitReason.AreaNotReadyUnknown;
            if (!AreaSeen && ((!AfterDeath && Logout) || Custom) &&
                !float.IsNaN(WaitAtComparison) && !float.IsInfinity(WaitAtComparison) &&
                !float.IsNaN(Minimum) && !float.IsInfinity(Minimum) && WaitAtComparison <= Minimum)
                return LoadingWaitReason.MinimumDelayConsistent;
            return LoadingWaitReason.FallbackOrOther;
        }
    }

    internal static class LoadingDetailsTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".LoadingDetailsTelemetry");
        private static readonly string[] StageNames = { "VerifyBiomeData", "TryLoadCache", "GenerateBiomePoints", "GenerateSectors" };
        private static readonly string[] ReasonNames = Enum.GetNames(typeof(LoadingWaitReason));
        private static readonly Stage[] Stages = new Stage[4];
        private static readonly long[] Reasons = new long[7];
        private static AccessTools.FieldRef<Game, float> nativeWait = null!, nativeMinimum = null!;
        private static AccessTools.FieldRef<Game, bool> afterDeath = null!;
        private static int ownerThread;
        private static long probeFailures, otherThreadSkips;
        private static string latestReason = "unobserved";
        private static float lastWait, lastMinimum;
        private static bool waitObserved;
        [ThreadStatic] private static bool finding;
        [ThreadStatic] private static LoadingWaitEvidence evidence;
        [ThreadStatic] private static int areaDepth, zoneResult;
        internal static bool Enabled { get; set; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        internal struct Stage
        {
            internal long Calls, Failures, TrueReturns, FalseReturns;
            internal double SumMs, MaxMs, LastMs;
            internal string? StartedUtc;
        }
        internal struct CostScope { internal bool Active; internal int Index; internal double Started; internal string? Utc; }
        internal struct FindScope { internal bool Active, PreviousFinding; internal LoadingWaitEvidence Previous; }
        internal struct AreaScope { internal bool Active; internal int PreviousDepth, PreviousZone; }

        internal static void Install(ManualLogSource logger)
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                nativeWait = AccessTools.FieldRefAccess<Game, float>("m_respawnWait");
                nativeMinimum = AccessTools.FieldRefAccess<Game, float>("m_respawnLoadDuration");
                afterDeath = AccessTools.FieldRefAccess<Game, bool>("m_respawnAfterDeath");
                for (int i = 0; i < StageNames.Length; i++)
                {
                    var method = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), StageNames[i], i == 3 ? Type.EmptyTypes : new[] { typeof(World) });
                    if (method == null || method.ReturnType != (i == 1 ? typeof(bool) : typeof(void)) || method.IsStatic != (i != 3))
                        throw new InvalidOperationException("Unsupported biome signature.");
                    Patch(method, nameof(BeforeCost), i == 1 ? nameof(AfterCache) : nameof(AfterCost));
                }
                var find = AccessTools.DeclaredMethod(typeof(Game), "FindSpawnPoint", new[] { typeof(Vector3).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(float) });
                var area = AccessTools.DeclaredMethod(typeof(ZNetScene), "IsAreaReady", new[] { typeof(Vector3) });
                if (find == null || area == null || find.ReturnType != typeof(bool) || area.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unsupported readiness signature.");
                Patch(find, nameof(BeforeFind), nameof(AfterFind), nameof(ObserveNativeResults));
                Patch(area, nameof(BeforeArea), nameof(AfterArea), nameof(ObserveNativeResults));
                Installed = true; Status = "installed";
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf(); Installed = Enabled = false; Status = "unavailable";
                logger.LogWarning("Loading details unavailable: " + exception.GetType().Name);
            }
        }
        private static void Patch(MethodInfo method, string prefix, string finalizer, string? transpiler = null)
        {
            Patches.Patch(method, prefix: new HarmonyMethod(typeof(LoadingDetailsTelemetry), prefix),
                finalizer: new HarmonyMethod(typeof(LoadingDetailsTelemetry), finalizer) { priority = Priority.Last },
                transpiler: transpiler == null ? null : new HarmonyMethod(typeof(LoadingDetailsTelemetry), transpiler));
        }
        private static bool Observe()
        {
            if (!Enabled || !Installed) return false;
            if (Thread.CurrentThread.ManagedThreadId == ownerThread) return true;
            Interlocked.Increment(ref otherThreadSkips); return false;
        }
        private static void BeforeCost(MethodBase __originalMethod, out CostScope __state)
        {
            __state = default;
            try
            {
                if (!Observe()) return;
                int index = Array.IndexOf(StageNames, __originalMethod.Name);
                if (index >= 0) __state = new CostScope { Active = true, Index = index, Started = Now, Utc = DateTime.UtcNow.ToString("O") };
            }
            catch { probeFailures++; }
        }
        private static void Record(CostScope scope, Exception? error, bool? returned)
        {
            if (!scope.Active || !Observe()) return;
            double ms = (Now - scope.Started) * 1000;
            if (ms < 0 || double.IsNaN(ms) || double.IsInfinity(ms)) { probeFailures++; return; }
            var stage = Stages[scope.Index]; stage.Calls++; if (error != null) stage.Failures++;
            else if (returned.HasValue) { if (returned.Value) stage.TrueReturns++; else stage.FalseReturns++; }
            stage.SumMs += ms; stage.MaxMs = Math.Max(stage.MaxMs, ms); stage.LastMs = ms; stage.StartedUtc = scope.Utc;
            Stages[scope.Index] = stage;
        }
        private static void AfterCost(CostScope __state, Exception? __exception)
        { try { Record(__state, __exception, null); } catch { probeFailures++; } }
        private static void AfterCache(bool __result, CostScope __state, Exception? __exception)
        { try { Record(__state, __exception, __result); } catch { probeFailures++; } }
        private static void BeforeFind(Game __instance, float __2, out FindScope __state)
        {
            __state = default;
            try
            {
                if (!Observe()) return;
                __state = new FindScope { Active = true, PreviousFinding = finding, Previous = evidence };
                evidence = new LoadingWaitEvidence { AfterDeath = afterDeath(__instance),
                    WaitAtComparison = nativeWait(__instance) + __2, Minimum = nativeMinimum(__instance) };
                finding = true;
            }
            catch { probeFailures++; }
        }
        private static void AfterFind(Game __instance, bool __result, FindScope __state, Exception? __exception)
        {
            try
            {
                if (!__state.Active || !Observe()) return;
                var reason = evidence.Classify(__result, __exception != null);
                Reasons[(int)reason]++; latestReason = ReasonNames[(int)reason];
                lastWait = nativeWait(__instance); lastMinimum = evidence.Minimum; waitObserved = true;
            }
            catch { probeFailures++; }
            finally { if (__state.Active) { finding = __state.PreviousFinding; evidence = __state.Previous; } }
        }
        private static void BeforeArea(out AreaScope __state)
        {
            __state = default;
            if (!finding || !Observe()) return;
            __state = new AreaScope { Active = true, PreviousDepth = areaDepth, PreviousZone = zoneResult };
            areaDepth++; zoneResult = 0;
        }
        private static void AfterArea(bool __result, AreaScope __state, Exception? __exception)
        {
            try
            {
                if (__state.Active && Observe() && __exception == null)
                { evidence.AreaSeen = true; evidence.AreaReady = __result; evidence.Zone = zoneResult; }
            }
            catch { probeFailures++; }
            finally { if (__state.Active) { areaDepth = __state.PreviousDepth; zoneResult = __state.PreviousZone; } }
        }
        private static bool LogoutObserved(bool value) { if (Enabled && finding) evidence.Logout = value; return value; }
        private static bool CustomObserved(bool value) { if (Enabled && finding) evidence.Custom = value; return value; }
        private static bool ZoneObserved(bool value) { if (Enabled && areaDepth > 0) zoneResult = value ? 1 : 2; return value; }
        private static IEnumerable<CodeInstruction> ObserveNativeResults(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var targets = __originalMethod.DeclaringType == typeof(Game)
                ? new[] { AccessTools.DeclaredMethod(typeof(PlayerProfile), "HaveLogoutPoint", Type.EmptyTypes), AccessTools.DeclaredMethod(typeof(PlayerProfile), "HaveCustomSpawnPoint", Type.EmptyTypes) }
                : new[] { AccessTools.DeclaredMethod(typeof(ZoneSystem), "IsZoneLoaded", new[] { typeof(Vector2s) }) };
            var observers = targets.Length == 2 ? new[] { nameof(LogoutObserved), nameof(CustomObserved) } : new[] { nameof(ZoneObserved) };
            foreach (var target in targets)
            {
                if (target == null || target.ReturnType != typeof(bool) || code.Count(c => c.Calls(target)) != 1)
                    throw new InvalidOperationException("Unsupported loading evidence callsite.");
                int index = code.FindIndex(c => c.Calls(target));
                if (index + 1 >= code.Count || code[index + 1].opcode.FlowControl != FlowControl.Cond_Branch)
                    throw new InvalidOperationException("Unsupported loading evidence branch.");
            }
            foreach (var instruction in code)
            {
                yield return instruction;
                for (int i = 0; i < targets.Length; i++) if (instruction.Calls(targets[i]))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LoadingDetailsTelemetry), observers[i]));
            }
        }
        internal static void StartCapture() { } // Keep costs incurred before capture creation.
        internal static void Reset()
        {
            Array.Clear(Stages, 0, Stages.Length); Array.Clear(Reasons, 0, Reasons.Length);
            probeFailures = otherThreadSkips = 0; latestReason = "unobserved"; waitObserved = finding = false; areaDepth = zoneResult = 0;
        }
        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("loading_details_status", Enabled ? Status : "disabled"));
            labels.Add(new TextValue("loading_details_semantics", "process_cumulative; precapture_preserved; inclusive_nested_elapsed_not_CPU; cache_true_does_not_exclude_regeneration"));
            labels.Add(new TextValue("loading_wait_last_observed_reason", latestReason));
            labels.Add(new TextValue("loading_wait_reason_semantics", "counts_of_native_checks_not_wait_duration; minimum_consistent_with_native_game_dt_gate; loaded_zone_area_false_native_missing_valid_instance; foreign_patches_may_change_causes"));
            gauges.Add(new NumberValue("loading_details_probe_failures_total", probeFailures, "calls"));
            gauges.Add(new NumberValue("loading_details_other_thread_skips_total", otherThreadSkips, "calls"));
            for (int i = 0; i < Stages.Length; i++)
            {
                var stage = Stages[i]; if (stage.Calls == 0) continue;
                string key = "loading_biome_" + StageNames[i];
                gauges.Add(new NumberValue(key + "_calls_total", stage.Calls, "calls"));
                gauges.Add(new NumberValue(key + "_failures_total", stage.Failures, "calls"));
                gauges.Add(new NumberValue(key + "_sum_total", stage.SumMs, "ms"));
                gauges.Add(new NumberValue(key + "_max", stage.MaxMs, "ms"));
                gauges.Add(new NumberValue(key + "_last", stage.LastMs, "ms"));
                labels.Add(new TextValue(key + "_last_started_utc", stage.StartedUtc ?? "unavailable"));
                if (i == 1)
                {
                    gauges.Add(new NumberValue(key + "_true_total", stage.TrueReturns, "calls"));
                    gauges.Add(new NumberValue(key + "_false_total", stage.FalseReturns, "calls"));
                }
            }
            for (int i = 0; i < Reasons.Length; i++) if (Reasons[i] > 0)
                gauges.Add(new NumberValue("loading_wait_" + ((LoadingWaitReason)i) + "_total", Reasons[i], "checks"));
            if (waitObserved)
            {
                if (!float.IsNaN(lastWait) && !float.IsInfinity(lastWait)) gauges.Add(new NumberValue("loading_native_respawn_wait_last", lastWait, "game_seconds"));
                if (!float.IsNaN(lastMinimum) && !float.IsInfinity(lastMinimum)) gauges.Add(new NumberValue("loading_native_respawn_minimum_last", lastMinimum, "game_seconds"));
            }
        }
        internal static void Uninstall() { Patches.UnpatchSelf(); Installed = Enabled = false; Status = "disabled"; Reset(); }
    }
}
