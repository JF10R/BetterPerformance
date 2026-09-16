using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Counts of native ownership and replication calls. Every hook is a parameterless
    // prefix that only increments a counter: no allocation, no game state is read or
    // changed, and the original method always runs.
    internal static class OwnershipTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".OwnershipTelemetry");
        private static AccessTools.FieldRef<ZDOMan, int>? zdosSent, zdosRecv, zdosSentLastSec, zdosRecvLastSec;
        private static FieldInfo? changeQueue, deadZdos, objectsById;
        private static PropertyInfo? changeQueueCount, deadZdosCount, objectsByIdCount;
        private static string fieldStatus = "unavailable";
        private static int ownerThread;
        private static long setOwnerCalls, requestZdoCalls, requestOwnCalls, requestOpenCalls;
        private static long otherThreadSkips, probeFailures;
        private static int previousSent, previousRecv;
        private static bool baseline;

        internal static bool Enabled { get; set; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ManualLogSource logger)
        {
            try
            {
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                Patch(typeof(ZDO), "SetOwner", new[] { typeof(long) }, nameof(OnSetOwner));
                Patch(typeof(ZDOMan), "RPC_RequestZDO", new[] { typeof(long), typeof(ZDOID) }, nameof(OnRequestZdo));
                Patch(typeof(ItemDrop), "RPC_RequestOwn", new[] { typeof(long) }, nameof(OnRequestOwn));
                Patch(typeof(Container), "RPC_RequestOpen", new[] { typeof(long), typeof(long) }, nameof(OnRequestOpen));
                ResolveFields();
                Installed = true;
                Status = "installed";
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                Patches.UnpatchSelf();
                logger.LogWarning("Ownership telemetry unavailable: " + exception.GetType().Name);
            }
        }

        private static void Patch(Type type, string name, Type[] parameters, string prefix)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported ownership signature: " + type.Name + "." + name);
            Patches.Patch(method, prefix: new HarmonyMethod(typeof(OwnershipTelemetry), prefix));
        }

        // Counter fields are optional: a shape change degrades the gauges, never the hooks.
        private static void ResolveFields()
        {
            try
            {
                zdosSent = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosSent");
                zdosRecv = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosRecv");
                zdosSentLastSec = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosSentLastSec");
                zdosRecvLastSec = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosRecvLastSec");
                changeQueue = Collection("m_clientChangeQueue", out changeQueueCount);
                deadZdos = Collection("m_deadZDOs", out deadZdosCount);
                objectsById = Collection("m_objectsByID", out objectsByIdCount);
                fieldStatus = changeQueue != null && deadZdos != null && objectsById != null ? "available" : "partial";
            }
            catch
            {
                zdosSent = zdosRecv = zdosSentLastSec = zdosRecvLastSec = null;
                fieldStatus = "unavailable";
            }
        }

        private static FieldInfo? Collection(string name, out PropertyInfo? count)
        {
            count = null;
            var field = AccessTools.Field(typeof(ZDOMan), name);
            if (field == null || field.FieldType.IsValueType) return null;
            count = field.FieldType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            return count != null && count.PropertyType == typeof(int) ? field : null;
        }

        private static bool Observe()
        {
            if (!Enabled || !Installed) return false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            {
                Interlocked.Increment(ref otherThreadSkips);
                return false;
            }
            return true;
        }

        private static void OnSetOwner() { if (Observe()) setOwnerCalls++; }
        private static void OnRequestZdo() { if (Observe()) requestZdoCalls++; }
        private static void OnRequestOwn() { if (Observe()) requestOwnCalls++; }
        private static void OnRequestOpen() { if (Observe()) requestOpenCalls++; }

        internal static void Reset()
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Exchange(ref setOwnerCalls, 0);
            Interlocked.Exchange(ref requestZdoCalls, 0);
            Interlocked.Exchange(ref requestOwnCalls, 0);
            Interlocked.Exchange(ref requestOpenCalls, 0);
            Interlocked.Exchange(ref otherThreadSkips, 0);
            Interlocked.Exchange(ref probeFailures, 0);
            previousSent = previousRecv = 0;
            baseline = false;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("zdo_set_owner_calls", Interlocked.Exchange(ref setOwnerCalls, 0), "calls"));
            gauges.Add(new NumberValue("zdo_request_rpcs", Interlocked.Exchange(ref requestZdoCalls, 0), "calls"));
            gauges.Add(new NumberValue("item_request_own_rpcs", Interlocked.Exchange(ref requestOwnCalls, 0), "calls"));
            gauges.Add(new NumberValue("container_open_requests", Interlocked.Exchange(ref requestOpenCalls, 0), "calls"));
            gauges.Add(new NumberValue("ownership_other_thread_skips", Interlocked.Exchange(ref otherThreadSkips, 0), "calls"));
            labels.Add(new TextValue("ownership_telemetry_status", !Enabled ? "disabled" : Status));
            labels.Add(new TextValue("ownership_scope", "counts_of_native_calls_not_latency; request_completion_latency_is_in_action_telemetry"));
            string replication = SampleReplication(gauges);
            gauges.Add(new NumberValue("ownership_probe_failures", Interlocked.Exchange(ref probeFailures, 0), "calls"));
            labels.Add(new TextValue("zdoman_counters_status", replication));
        }

        private static string SampleReplication(List<NumberValue> gauges)
        {
            if (fieldStatus == "unavailable") return "unavailable";
            var manager = ZDOMan.instance;
            if (manager == null) return "no_zdoman";
            try
            {
                if (zdosSent != null && zdosRecv != null)
                {
                    int sent = zdosSent(manager), received = zdosRecv(manager);
                    gauges.Add(new NumberValue("zdoman_zdos_sent_total", sent, "zdos"));
                    gauges.Add(new NumberValue("zdoman_zdos_recv_total", received, "zdos"));
                    if (baseline)
                    {
                        // A new ZDOMan or a native counter reset makes a delta meaningless.
                        gauges.Add(new NumberValue("zdoman_zdos_sent_delta", Math.Max(0, sent - previousSent), "zdos"));
                        gauges.Add(new NumberValue("zdoman_zdos_recv_delta", Math.Max(0, received - previousRecv), "zdos"));
                    }
                    previousSent = sent;
                    previousRecv = received;
                    baseline = true;
                }
                if (zdosSentLastSec != null) gauges.Add(new NumberValue("zdoman_zdos_sent_last_sec", zdosSentLastSec(manager), "zdos_per_second"));
                if (zdosRecvLastSec != null) gauges.Add(new NumberValue("zdoman_zdos_recv_last_sec", zdosRecvLastSec(manager), "zdos_per_second"));
                Count(gauges, "zdoman_client_change_queue", changeQueue, changeQueueCount, manager, "entries");
                Count(gauges, "zdoman_dead_zdos", deadZdos, deadZdosCount, manager, "entries");
                Count(gauges, "zdoman_objects_total", objectsById, objectsByIdCount, manager, "zdos");
                return fieldStatus;
            }
            catch
            {
                Interlocked.Increment(ref probeFailures);
                return "read_failed";
            }
        }

        private static void Count(List<NumberValue> gauges, string name, FieldInfo? field, PropertyInfo? count, ZDOMan manager, string unit)
        {
            if (field == null || count == null) return;
            object? value = field.GetValue(manager);
            if (value == null) return;
            gauges.Add(new NumberValue(name, (int)count.GetValue(value, null)!, unit));
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Reset();
            Patches.UnpatchSelf();
            Installed = false;
            Status = "disabled";
            fieldStatus = "unavailable";
            zdosSent = zdosRecv = zdosSentLastSec = zdosRecvLastSec = null;
            changeQueue = deadZdos = objectsById = null;
            changeQueueCount = deadZdosCount = objectsByIdCount = null;
        }
    }
}
