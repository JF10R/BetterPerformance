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

        // Which native method is running while ZDO.SetOwner is called. Set by a
        // prefix/finalizer pair on each caller, read by the SetOwner prefix. A finalizer,
        // not a postfix, so a throwing native call cannot leave the marker set.
        private enum OwnerCaller { Other, ReleaseServerPass, ReleasePeerPass, ZdoDataReapply, DisconnectSweep, InvalidPrefabDestroy }
        [ThreadStatic] private static OwnerCaller caller;

        private static readonly List<string> MissingMarkers = new List<string>();
        private static AccessTools.FieldRef<ZDOMan, long>? sessionId;
        private static string markerStatus = "unavailable";
        private static long releaseToZero, releasePeerClaims, releaseServerClaims;
        private static long zdoDataReapply, disconnectSweep, invalidPrefabDestroy, otherCallers;
        private static long releaseCycles, releaseCycleReleased, releaseCycleReclaimed;
        private static long releaseCycleNetChanges, releaseCycleCapacitySkips;

        // One release cycle is one ZDOMan.ReleaseZDOS call: a server pass followed by one
        // pass per peer, all inside a single Update. The map holds the first owner seen and
        // the last owner written for each ZDO the cycle touched, which is enough to count
        // net transitions without re-reading the game at flush time.
        private const int CycleCapacity = 65536;
        private struct CycleEntry { internal long First, Last; internal bool Released; }
        private static readonly Dictionary<ZDOID, CycleEntry> cycle = new Dictionary<ZDOID, CycleEntry>();
        private static bool cycleOpen, cycleTouched;
        private static int cycleNet;

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
                ResolveCallerMarkers();
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

        // Caller markers are optional and installed one at a time: a changed game signature
        // degrades its own split counter to zero and is named in the export, never the module.
        private static void ResolveCallerMarkers()
        {
            MissingMarkers.Clear();
            Marker("release_cycle", () =>
                Scope(typeof(ZDOMan), "ReleaseZDOS", new[] { typeof(float) },
                    nameof(BeforeReleaseZdos), nameof(AfterReleaseZdos)));
            // The pass marker needs the session id to tell the server's own reference pass
            // from a peer pass, so the two are installed together or not at all.
            Marker("release_pass", () =>
            {
                sessionId = AccessTools.FieldRefAccess<ZDOMan, long>("m_sessionID");
                Scope(typeof(ZDOMan), "ReleaseNearbyZDOS", new[] { typeof(UnityEngine.Vector3), typeof(long) },
                    nameof(BeforeReleasePass), nameof(AfterScope));
            });
            Marker("zdo_data", () =>
                Scope(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) },
                    nameof(BeforeZdoData), nameof(AfterScope)));
            Marker("disconnect_sweep", () =>
                Scope(typeof(ZDOMan), "RemovePeer", new[] { GameType("ZNetPeer") },
                    nameof(BeforeDisconnectSweep), nameof(AfterScope)));
            Marker("invalid_prefab", () =>
            {
                Type[] create = { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() };
                Scope(typeof(ZNetScene), "CreateObjectsSorted", create, nameof(BeforeInvalidPrefab), nameof(AfterScope));
                Scope(typeof(ZNetScene), "CreateDistantObjects", create, nameof(BeforeInvalidPrefab), nameof(AfterScope));
            });
            markerStatus = MissingMarkers.Count == 0 ? "installed" : "partial";
        }

        private static void Marker(string name, Action install)
        {
            try { install(); }
            catch (Exception exception)
            {
                if (MissingMarkers.Count < 16) MissingMarkers.Add(name + ":" + exception.GetType().Name);
            }
        }

        private static Type GameType(string name) => typeof(ZNet).Assembly.GetType(name, false)
            ?? throw new InvalidOperationException("Game type is unavailable: " + name);

        // A prefix/finalizer pair that only moves a thread-static marker. Neither half can
        // skip the original, read its arguments' contents, or replace its result.
        private static void Scope(Type type, string name, Type[] parameters, string prefix, string finalizer)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported ownership scope: " + type.Name + "." + name);
            Patches.Patch(method, prefix: new HarmonyMethod(typeof(OwnershipTelemetry), prefix),
                finalizer: new HarmonyMethod(typeof(OwnershipTelemetry), finalizer));
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

        // __0 is the owner the native call is about to write; the ZDO is read, never changed.
        private static void OnSetOwner(ZDO __instance, long __0)
        {
            if (!Observe()) return;
            setOwnerCalls++;
            switch (caller)
            {
                case OwnerCaller.ReleaseServerPass:
                    if (__0 == 0L) releaseToZero++; else releaseServerClaims++;
                    Stage(__instance, __0);
                    break;
                case OwnerCaller.ReleasePeerPass:
                    if (__0 == 0L) releaseToZero++; else releasePeerClaims++;
                    Stage(__instance, __0);
                    break;
                case OwnerCaller.ZdoDataReapply: zdoDataReapply++; break;
                case OwnerCaller.DisconnectSweep: disconnectSweep++; break;
                case OwnerCaller.InvalidPrefabDestroy: invalidPrefabDestroy++; break;
                default: otherCallers++; break;
            }
        }

        // Records one owner transition inside the open cycle. The net count is maintained
        // incrementally, so closing a cycle reads nothing back from the game.
        private static void Stage(ZDO zdo, long uid)
        {
            if (!cycleOpen) return;
            try
            {
                if (uid == 0L) releaseCycleReleased++;
                ZDOID id = zdo.m_uid;
                if (cycle.TryGetValue(id, out CycleEntry entry))
                {
                    bool wasNet = entry.First != entry.Last;
                    if (uid == 0L) entry.Released = true;
                    else if (entry.Released) { releaseCycleReclaimed++; entry.Released = false; }
                    entry.Last = uid;
                    bool isNet = entry.First != entry.Last;
                    if (wasNet != isNet) cycleNet += isNet ? 1 : -1;
                    cycle[id] = entry;
                    return;
                }
                if (cycle.Count >= CycleCapacity) { releaseCycleCapacitySkips++; return; }
                var fresh = new CycleEntry { First = zdo.GetOwner(), Last = uid, Released = uid == 0L };
                if (fresh.First != fresh.Last) cycleNet++;
                cycle.Add(id, fresh);
            }
            catch { probeFailures++; }
        }

        private static void BeforeReleaseZdos(out OwnerCaller __state)
        {
            __state = caller;
            if (!Enabled || !Installed) return;
            cycle.Clear();
            cycleNet = 0;
            cycleTouched = false;
            cycleOpen = true;
        }

        // ReleaseZDOS is called every Update but only does work every two seconds, so a
        // cycle is counted only when at least one pass ran inside it.
        private static void AfterReleaseZdos(OwnerCaller __state)
        {
            caller = __state;
            if (!cycleOpen) return;
            cycleOpen = false;
            if (cycleTouched)
            {
                releaseCycles++;
                releaseCycleNetChanges += cycleNet;
            }
            cycle.Clear();
        }

        private static void BeforeReleasePass(ZDOMan __instance, long __1, out OwnerCaller __state)
        {
            __state = caller;
            if (!Enabled || !Installed || sessionId == null) return;
            caller = __1 == sessionId(__instance) ? OwnerCaller.ReleaseServerPass : OwnerCaller.ReleasePeerPass;
            cycleTouched = true;
        }

        private static void BeforeZdoData(out OwnerCaller __state)
        { __state = caller; if (Enabled && Installed) caller = OwnerCaller.ZdoDataReapply; }
        private static void BeforeDisconnectSweep(out OwnerCaller __state)
        { __state = caller; if (Enabled && Installed) caller = OwnerCaller.DisconnectSweep; }
        private static void BeforeInvalidPrefab(out OwnerCaller __state)
        { __state = caller; if (Enabled && Installed) caller = OwnerCaller.InvalidPrefabDestroy; }
        private static void AfterScope(OwnerCaller __state) { caller = __state; }

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
            releaseToZero = releasePeerClaims = releaseServerClaims = 0;
            zdoDataReapply = disconnectSweep = invalidPrefabDestroy = otherCallers = 0;
            releaseCycles = releaseCycleReleased = releaseCycleReclaimed = 0;
            releaseCycleNetChanges = releaseCycleCapacitySkips = 0;
            cycle.Clear();
            cycleOpen = cycleTouched = false;
            cycleNet = 0;
            caller = OwnerCaller.Other;
            previousSent = previousRecv = 0;
            baseline = false;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("zdo_set_owner_calls", Interlocked.Exchange(ref setOwnerCalls, 0), "calls"));
            gauges.Add(new NumberValue("zdo_request_rpcs", Interlocked.Exchange(ref requestZdoCalls, 0), "calls"));
            gauges.Add(new NumberValue("item_request_own_rpcs", Interlocked.Exchange(ref requestOwnCalls, 0), "calls"));
            gauges.Add(new NumberValue("container_open_requests", Interlocked.Exchange(ref requestOpenCalls, 0), "calls"));
            // The seven-way split of zdo_set_owner_calls, keyed by the native method that
            // performed the write. The parent counter keeps its original meaning: every call.
            gauges.Add(new NumberValue("zdo_set_owner_calls_release_to_zero", Interlocked.Exchange(ref releaseToZero, 0), "calls"));
            gauges.Add(new NumberValue("zdo_set_owner_calls_release_claim_peer", Interlocked.Exchange(ref releasePeerClaims, 0), "calls"));
            gauges.Add(new NumberValue("zdo_set_owner_calls_release_server_pass", Interlocked.Exchange(ref releaseServerClaims, 0), "calls"));
            gauges.Add(new NumberValue("zdo_set_owner_calls_zdo_data_reapply", Interlocked.Exchange(ref zdoDataReapply, 0), "calls"));
            gauges.Add(new NumberValue("zdo_set_owner_calls_disconnect_sweep", Interlocked.Exchange(ref disconnectSweep, 0), "calls"));
            gauges.Add(new NumberValue("zdo_set_owner_calls_invalid_prefab_destroy", Interlocked.Exchange(ref invalidPrefabDestroy, 0), "calls"));
            gauges.Add(new NumberValue("zdo_set_owner_calls_other", Interlocked.Exchange(ref otherCallers, 0), "calls"));
            gauges.Add(new NumberValue("release_cycles", Interlocked.Exchange(ref releaseCycles, 0), "cycles"));
            gauges.Add(new NumberValue("release_cycle_released", Interlocked.Exchange(ref releaseCycleReleased, 0), "zdos"));
            gauges.Add(new NumberValue("release_cycle_reclaimed", Interlocked.Exchange(ref releaseCycleReclaimed, 0), "zdos"));
            gauges.Add(new NumberValue("release_cycle_net_changes", Interlocked.Exchange(ref releaseCycleNetChanges, 0), "zdos"));
            gauges.Add(new NumberValue("release_cycle_capacity_skips", Interlocked.Exchange(ref releaseCycleCapacitySkips, 0), "zdos"));
            gauges.Add(new NumberValue("ownership_other_thread_skips", Interlocked.Exchange(ref otherThreadSkips, 0), "calls"));
            labels.Add(new TextValue("ownership_telemetry_status", !Enabled ? "disabled" : Status));
            labels.Add(new TextValue("ownership_caller_split_status", !Enabled ? "disabled" : markerStatus));
            labels.Add(new TextValue("ownership_markers_unavailable",
                MissingMarkers.Count == 0 ? "none" : string.Join(",", MissingMarkers.ToArray())));
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
            fieldStatus = markerStatus = "unavailable";
            MissingMarkers.Clear();
            sessionId = null;
            zdosSent = zdosRecv = zdosSentLastSec = zdosRecvLastSec = null;
            changeQueue = deadZdos = objectsById = null;
            changeQueueCount = deadZdosCount = objectsByIdCount = null;
        }
    }
}
