using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BetterPerformance.Core;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Aggregate observations only: never enumerate NPCs or retain game objects.
    internal static class AiTelemetry
    {
        private static readonly AiCadence Cadence = new AiCadence();
        private static CaptureSession? capture;
        private static int mainThread;
        private static long pathReturns, pathFalse;
        private static string batchStatus = "disabled", pathResultStatus = "disabled";

        internal struct BatchState
        {
            internal CaptureSession? Session;
            internal long Started;
        }

        internal static void Install(Harmony harmony, ManualLogSource logger, bool enabled)
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            if (enabled)
            {
                try
                {
                    var assembly = typeof(ZNet).Assembly;
                    Type? updater = assembly.GetType("MonoUpdatersExtra"), ai = assembly.GetType("IUpdateAI");
                    if (updater == null || ai == null) batchStatus = "unavailable";
                    else
                    {
                        Type list = typeof(List<>).MakeGenericType(ai);
                        var method = AccessTools.DeclaredMethod(updater, "UpdateAI", new[] { list, list, typeof(string), typeof(float) });
                        if (method == null || method.ReturnType != typeof(void) || !method.IsStatic) batchStatus = "unavailable";
                        else
                        {
                            harmony.Patch(method, prefix: new HarmonyMethod(typeof(AiTelemetry), nameof(BatchPrefix)),
                                finalizer: new HarmonyMethod(typeof(AiTelemetry), nameof(BatchFinalizer)));
                            batchStatus = "enabled";
                        }
                    }
                }
                catch (Exception exception)
                { batchStatus = "patch_failed"; logger.LogWarning("AI batch probe unavailable: " + exception.GetType().Name); }
                try
                {
                    var method = AccessTools.DeclaredMethod(typeof(Pathfinding), "GetPath", PathArguments);
                    if (method == null || method.ReturnType != typeof(bool)) pathResultStatus = "unavailable";
                    else
                    {
                        harmony.Patch(method, postfix: new HarmonyMethod(typeof(AiTelemetry), nameof(PathResult)));
                        pathResultStatus = "enabled";
                    }
                }
                catch (Exception exception)
                { pathResultStatus = "patch_failed"; logger.LogWarning("Path result probe unavailable: " + exception.GetType().Name); }
            }
            TimingHooks.Availability.Add(new TextValue("probe.AiBatch", batchStatus));
            TimingHooks.Availability.Add(new TextValue("probe.PathQueryResult", pathResultStatus));
        }

        internal static readonly Type[] PathArguments = { typeof(Vector3), typeof(Vector3), typeof(List<Vector3>),
            typeof(Pathfinding.AgentType), typeof(bool), typeof(bool), typeof(bool) };

        private static CaptureSession? Active()
        {
            var session = Volatile.Read(ref TimingHooks.Current);
            if (session == null) return null;
            if (Thread.CurrentThread.ManagedThreadId != mainThread)
            { session.RecordProbeFailure(); return null; }
            if (!ReferenceEquals(capture, session))
            {
                Reset();
                capture = session;
            }
            return session;
        }

        private static void BatchPrefix(ICollection __0, ICollection __1, float __3, out BatchState __state)
        {
            __state = default;
            CaptureSession? session = null;
            try
            {
                session = Active();
                if (session == null) return;
                long started = Stopwatch.GetTimestamp();
                __state = new BatchState { Session = session, Started = started };
                if (!Cadence.Observe(session.Clock.ElapsedSeconds(started), Time.frameCount,
                    __1.Count, __0.Count, __3)) session.RecordProbeFailure();
            }
            catch { session?.RecordProbeFailure(); }
        }

        // Void finalizer preserves the native exception. Timing includes the whole
        // dispatch batch, nested AI/path work and the small prefix observation.
        private static void BatchFinalizer(BatchState __state, Exception? __exception)
        {
            if (__state.Session == null) return;
            try
            {
                __state.Session.Book.Record(Metric.AiBatch,
                    (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency, __exception != null);
            }
            catch { __state.Session.RecordProbeFailure(); }
        }

        private static void PathResult(bool __result)
        {
            CaptureSession? session = null;
            try
            {
                session = Active();
                if (session == null) return;
                pathReturns++;
                if (!__result) pathFalse++;
            }
            catch { session?.RecordProbeFailure(); }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            AiCadenceSnapshot observed = Cadence.Drain();
            labels.Add(new TextValue("ai_batch_probe_status", batchStatus));
            labels.Add(new TextValue("path_query_result_probe_status", pathResultStatus));
            labels.Add(new TextValue("ai_observation_scope", "local_batch; input_includes_unowned; repeated_frame_is_not_ai_quality"));
            labels.Add(new TextValue("ai_cadence_scope", "supplied_dt_not_wall_time; gaps_can_cross_export_boundary"));
            labels.Add(new TextValue("path_query_result_scope", "observed_normal_return; false_is_not_no_path_diagnosis"));
            labels.Add(new TextValue("spawn_observation_scope", "native_spawn_calls_not_creatures; UpdateSpawning_owner_and_local_player_gates"));
            gauges.Add(new NumberValue("ai_batches_observed", observed.Batches, "batches"));
            gauges.Add(new NumberValue("ai_batches_repeated_frame", observed.RepeatedFrameBatches, "batches"));
            gauges.Add(new NumberValue("ai_wall_gap_count", observed.GapCount, "gaps"));
            gauges.Add(new NumberValue("ai_observations_rejected", observed.Rejected, "observations"));
            if (observed.Batches > 0)
            {
                gauges.Add(new NumberValue("ai_input_count_last", observed.InputCountLast, "entries"));
                gauges.Add(new NumberValue("ai_input_count_max", observed.InputCountMax, "entries"));
                gauges.Add(new NumberValue("ai_input_count_sum", observed.InputCountSum, "entry_observations"));
                gauges.Add(new NumberValue("ai_scratch_count_last", observed.ScratchCountLast, "entries"));
                gauges.Add(new NumberValue("ai_scratch_count_max", observed.ScratchCountMax, "entries"));
                gauges.Add(new NumberValue("ai_supplied_dt_sum", observed.DeltaSumMs, "ms"));
                gauges.Add(new NumberValue("ai_supplied_dt_max", observed.DeltaMaxMs, "ms"));
            }
            if (observed.GapCount > 0)
            {
                gauges.Add(new NumberValue("ai_wall_gap_sum", observed.GapSumMs, "ms"));
                gauges.Add(new NumberValue("ai_wall_gap_max", observed.GapMaxMs, "ms"));
            }
            gauges.Add(new NumberValue("path_query_returns_observed", pathReturns, "calls"));
            gauges.Add(new NumberValue("path_query_false_results", pathFalse, "calls"));
            pathReturns = pathFalse = 0;
        }

        internal static void Finish(List<NumberValue> gauges, List<TextValue> labels)
        {
            Sample(gauges, labels);
            Reset();
        }

        internal static void Reset()
        {
            Cadence.Reset();
            pathReturns = pathFalse = 0;
            capture = null;
        }
    }
}
