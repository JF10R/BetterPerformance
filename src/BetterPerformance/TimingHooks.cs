using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BetterPerformance.Core;
using BepInEx.Logging;
using HarmonyLib;

namespace BetterPerformance
{
    internal static class TimingHooks
    {
        private static readonly Dictionary<MethodBase, Metric> Metrics = new Dictionary<MethodBase, Metric>();
        internal static readonly List<TextValue> Availability = new List<TextValue>();
        internal static CaptureSession? Current;

        internal static void Install(Harmony harmony, ManualLogSource logger, bool enabled)
        {
            Add(harmony, logger, enabled, typeof(ZNet), "Update", Metric.NetworkUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZDOMan), "Update", Metric.ReplicationUpdate, new[] { typeof(float) });
            Add(harmony, logger, enabled, typeof(ZNetScene), "Update", Metric.SceneUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZoneSystem), "Update", Metric.ZoneUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZNetScene), "CreateObjects", Metric.ObjectCreate,
                new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
            Add(harmony, logger, enabled, typeof(ZNetScene), "RemoveObjects", Metric.ObjectRemove,
                new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
            Add(harmony, logger, enabled, typeof(ZNet), "SaveWorld", Metric.SaveWorldCall, new[] { typeof(bool) });
            Add(harmony, logger, enabled, typeof(ZNet), "SaveWorldThread", Metric.SaveWorker, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZDOMan), "PrepareSave", Metric.SavePrepare, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZNet), "UpdatePeers", Metric.NetworkPeers, new[] { typeof(float) });
            Add(harmony, logger, enabled, typeof(ZNet), "UpdateSave", Metric.SaveUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZRpc), "Update", Metric.RpcUpdate, new[] { typeof(float) }, typeof(ZRpc.ErrorCode));
            Add(harmony, logger, enabled, typeof(ZNetScene), "CreateObjectsSorted", Metric.ObjectCreateSorted,
                new[] { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() });
            Add(harmony, logger, enabled, typeof(ZNetScene), "CreateDistantObjects", Metric.DistantObjectCreate,
                new[] { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() });
            string readiness = "disabled";
            if (enabled)
            {
                try
                {
                    var method = AccessTools.DeclaredMethod(typeof(ZoneSystem), "IsActiveAreaLoaded", Type.EmptyTypes);
                    if (method == null || method.ReturnType != typeof(bool)) readiness = "unavailable";
                    else
                    {
                        harmony.Patch(method, postfix: new HarmonyMethod(typeof(TimingHooks), nameof(ReadinessPostfix)));
                        readiness = "enabled";
                    }
                }
                catch (Exception exception) { readiness = "patch_failed"; logger.LogWarning("Zone readiness probe unavailable: " + exception.GetType().Name); }
            }
            Availability.Add(new TextValue("probe.ZoneReadiness", readiness));
        }

        private static void Add(Harmony harmony, ManualLogSource logger, bool enabled, Type type,
            string name, Metric metric, Type[] arguments, Type? returnType = null)
        {
            string status = "disabled";
            if (enabled)
            {
                try
                {
                    var method = AccessTools.DeclaredMethod(type, name, arguments);
                    if (method == null || method.ReturnType != (returnType ?? typeof(void))) status = "unavailable";
                    else
                    {
                        Metrics[method] = metric;
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(TimingHooks), nameof(Prefix)),
                            finalizer: new HarmonyMethod(typeof(TimingHooks), nameof(Finalizer)));
                        status = "enabled";
                    }
                }
                catch (Exception exception) { status = "patch_failed"; logger.LogWarning("Probe " + metric + " unavailable: " + exception.GetType().Name); }
            }
            Availability.Add(new TextValue("probe." + metric, status));
        }

        internal struct TimingState
        {
            internal CaptureSession? Session;
            internal long Started;
        }

        private static void Prefix(out TimingState __state)
        {
            var session = System.Threading.Volatile.Read(ref Current);
            __state = new TimingState { Session = session, Started = session == null ? 0 : Stopwatch.GetTimestamp() };
        }

        // A void finalizer observes failed calls without replacing or suppressing their exception.
        private static void Finalizer(MethodBase __originalMethod, TimingState __state, Exception? __exception)
        {
            if (__state.Session == null) return;
            try
            {
                if (Metrics.TryGetValue(__originalMethod, out var metric))
                    __state.Session.Book.Record(metric, (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency,
                        __exception != null);
            }
            catch { __state.Session.RecordProbeFailure(); }
        }

        private static void ReadinessPostfix(bool __result)
        {
            var session = System.Threading.Volatile.Read(ref Current);
            if (session != null) session.RecordReadiness(__result);
        }
    }
}
