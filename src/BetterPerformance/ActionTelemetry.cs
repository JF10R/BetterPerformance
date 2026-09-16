using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    internal static class ActionTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".ActionTelemetry");
        private static readonly ActionTracker<object> Tracker = new ActionTracker<object>(128, 30, new ReferenceComparer());
        private static AccessTools.FieldRef<Humanoid, Inventory>? inventory;
        private static AccessTools.FieldRef<InventoryGui, Container>? shownContainer;
        private static bool capturing;
        private static int ownerThread;
        private static Player? boundPlayer;
        private static Inventory? boundInventory;
        private static long probeFailures, otherThreadSkips, actionExceptions;
        [ThreadStatic] private static ItemDrop? requestItem;
        [ThreadStatic] private static PickupScope pickup;
        internal static bool Enabled { get; set; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct PickupScope
        {
            internal ItemDrop? Drop;
            internal ItemDrop.ItemData? Item;
            internal Inventory? Inventory;
            internal bool Resolved;
        }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

        internal static void Install(ManualLogSource logger)
        {
            try
            {
                inventory = AccessTools.FieldRefAccess<Humanoid, Inventory>("m_inventory");
                shownContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
                Patch(typeof(ItemDrop), "Pickup", new[] { typeof(Humanoid) }, typeof(void), nameof(BeforeRequest), nameof(AfterRequest));
                Patch(typeof(ItemDrop), "RequestOwn", Type.EmptyTypes, typeof(void), nameof(BeforeOwnership), null);
                Patch(typeof(Humanoid), "Pickup", new[] { typeof(GameObject), typeof(bool), typeof(bool) }, typeof(bool), nameof(BeforePickup), nameof(AfterPickup));
                Patch(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData) }, typeof(bool), null, nameof(AfterAddItem));
                Patch(typeof(Container), "Interact", new[] { typeof(Humanoid), typeof(bool), typeof(bool) }, typeof(bool), nameof(BeforeContainer), null);
                Patch(typeof(InventoryGui), "Show", new[] { typeof(Container), typeof(int) }, typeof(void), null, nameof(AfterShow));
                Patch(typeof(Container), "RPC_OpenResponse", new[] { typeof(long), typeof(bool) }, typeof(void), null, nameof(AfterOpenResponse));
                Installed = true; Status = "installed";
            }
            catch (Exception exception)
            {
                Installed = Enabled = false; Status = "unavailable";
                Patches.UnpatchSelf();
                logger.LogWarning("Local action telemetry unavailable: " + exception.GetType().Name);
            }
        }

        private static void Patch(Type type, string name, Type[] parameters, Type result, string? prefix, string? finalizer)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null || method.ReturnType != result || method.IsStatic) throw new InvalidOperationException("Unsupported action signature.");
            Patches.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(ActionTelemetry), prefix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ActionTelemetry), finalizer) { priority = Priority.Last });
        }

        private static bool Observe()
        {
            if (!Enabled || !capturing) return false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread) { Interlocked.Increment(ref otherThreadSkips); return false; }
            var local = Player.m_localPlayer;
            if (!ReferenceEquals(local, boundPlayer))
            {
                Tracker.CensorAll(); boundPlayer = local;
                boundInventory = ReferenceEquals(local, null) ? null : inventory!(local);
            }
            return !ReferenceEquals(boundPlayer, null) && !ReferenceEquals(boundInventory, null);
        }

        private static void BeforeRequest(ItemDrop __instance, Humanoid __0, out ItemDrop? __state)
        {
            __state = requestItem; requestItem = null;
            try
            {
                if (!Observe() || !ReferenceEquals(__0, boundPlayer)) return;
                requestItem = __instance;
                Tracker.BeginRequest(__instance, ObservedAction.Pickup, Now);
            }
            catch { probeFailures++; }
        }
        private static void AfterRequest(ItemDrop __instance, ItemDrop? __state, Exception? __exception)
        {
            try { if (__exception != null && Observe()) { Tracker.Censor(__instance); actionExceptions++; } }
            catch { probeFailures++; }
            finally { requestItem = __state; }
        }
        private static void BeforeOwnership(ItemDrop __instance)
        {
            try { if (Observe() && ReferenceEquals(__instance, requestItem)) Tracker.MarkOwnershipRequest(__instance); }
            catch { probeFailures++; }
        }

        private static void BeforePickup(Humanoid __instance, GameObject __0, out PickupScope __state)
        {
            __state = pickup; pickup = default;
            try
            {
                if (!Observe() || !ReferenceEquals(__instance, boundPlayer) || ReferenceEquals(__0, null)) return;
                // One lookup at an existing pickup call; never enumerate scene objects or inventories.
                var drop = __0.GetComponent<ItemDrop>();
                if (ReferenceEquals(drop, null) || ReferenceEquals(drop.m_itemData, null)) return;
                pickup = new PickupScope { Drop = drop, Item = drop.m_itemData, Inventory = boundInventory };
                Tracker.BeginDirect(drop, ObservedAction.Pickup, Now);
            }
            catch { probeFailures++; }
        }
        private static void AfterPickup(PickupScope __state, Exception? __exception)
        {
            try
            {
                if (Observe() && !ReferenceEquals(pickup.Drop, null))
                {
                    if (!pickup.Resolved) Tracker.Censor(pickup.Drop!);
                    if (__exception != null) actionExceptions++;
                }
            }
            catch { probeFailures++; }
            finally { pickup = __state; }
        }
        private static void AfterAddItem(Inventory __instance, ItemDrop.ItemData __0, bool __result, Exception? __exception)
        {
            try
            {
                if (!Observe() || pickup.Resolved || ReferenceEquals(pickup.Drop, null) ||
                    !ReferenceEquals(__instance, pickup.Inventory) || !ReferenceEquals(__0, pickup.Item)) return;
                if (__exception == null) Tracker.Complete(pickup.Drop!, ObservedAction.Pickup, Now, __result);
                else { Tracker.Censor(pickup.Drop!); actionExceptions++; }
                pickup.Resolved = true;
            }
            catch { probeFailures++; }
        }
        private static void BeforeContainer(Container __instance, Humanoid __0, bool __1)
        {
            try { if (!__1 && Observe() && ReferenceEquals(__0, boundPlayer)) Tracker.BeginRequest(__instance, ObservedAction.Container, Now); }
            catch { probeFailures++; }
        }
        private static void AfterShow(InventoryGui __instance, Container __0, Exception? __exception)
        {
            try
            {
                if (!Observe() || ReferenceEquals(__0, null)) return;
                if (__exception != null) { Tracker.Censor(__0); actionExceptions++; }
                else if (ReferenceEquals(shownContainer!(__instance), __0)) Tracker.Complete(__0, ObservedAction.Container, Now, true);
            }
            catch { probeFailures++; }
        }
        private static void AfterOpenResponse(Container __instance, bool __1, Exception? __exception)
        {
            try
            {
                if (!Observe()) return;
                if (__exception != null) { Tracker.Censor(__instance); actionExceptions++; }
                else if (!__1) Tracker.Complete(__instance, ObservedAction.Container, Now, false);
            }
            catch { probeFailures++; }
        }

        internal static void StartCapture() { Reset(); capturing = true; ownerThread = Thread.CurrentThread.ManagedThreadId; }
        internal static void Reset()
        {
            capturing = false; Tracker.Reset(); boundPlayer = null; boundInventory = null;
            requestItem = null; pickup = default; probeFailures = otherThreadSkips = actionExceptions = 0;
        }
        internal static void Finish(List<NumberValue> gauges, List<TextValue> labels)
        {
            Tracker.CensorAll(); Sample(gauges, labels); Reset();
        }
        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            // Rebind even when no actions occur: disappearance/replacement must censor
            // old pending references and update coverage before the next action hook.
            try { if (capturing && Thread.CurrentThread.ManagedThreadId == ownerThread) Observe(); }
            catch { probeFailures++; }
            if (!Enabled) Tracker.CensorAll();
            Tracker.Expire(Now);
            Append(gauges, "pickup", Tracker.Drain(ObservedAction.Pickup));
            Append(gauges, "container", Tracker.Drain(ObservedAction.Container));
            gauges.Add(new NumberValue("action_probe_failures", probeFailures, "calls"));
            gauges.Add(new NumberValue("action_other_thread_skips", Interlocked.Exchange(ref otherThreadSkips, 0), "calls"));
            gauges.Add(new NumberValue("action_observed_exception_callbacks", actionExceptions, "calls"));
            probeFailures = actionExceptions = 0;
            labels.Add(new TextValue("action_telemetry_status", Status));
            labels.Add(new TextValue("action_telemetry_coverage", !Enabled ? "disabled" : ReferenceEquals(boundPlayer, null) ? "no_local_player" : "bounded_local_observations"));
            labels.Add(new TextValue("pickup_trigger_classification", "unspecified"));
            labels.Add(new TextValue("action_acceptance_semantics", "AddItem_true_at_probe; false_may_partially_transfer; not_drop_disposal_or_peer_visibility"));
            labels.Add(new TextValue("action_ownership_semantics", "nested_RequestOwn_entry; wait_is_full_request_to_acceptance_subset; not_RPC_send_or_RTT"));
            labels.Add(new TextValue("action_zero_category_semantics", "omitted_category_is_zero_observed_interval_activity; not_complete_coverage"));
        }
        private static void Append(List<NumberValue> gauges, string kind, ActionTracker<object>.Summary summary)
        {
            if (summary.Pending == 0 && (summary.Started | summary.RequestCalls | summary.DirectCalls |
                summary.Confirmed | summary.Rejected | summary.Censored | summary.TimedOut | summary.Duplicates |
                summary.Unmatched | summary.CapacitySkipped | summary.OwnershipRequests | summary.AmbiguousConfirmed |
                summary.RequestCompleted | summary.DirectCompleted | summary.OwnershipCompleted) == 0 &&
                summary.RequestSumMs == 0 && summary.RequestMaxMs == 0 && summary.DirectSumMs == 0 &&
                summary.DirectMaxMs == 0 && summary.OwnershipSumMs == 0 && summary.OwnershipMaxMs == 0) return;
            string prefix = "action_" + kind + "_";
            void Add(string name, double value, string unit = "calls") => gauges.Add(new NumberValue(prefix + name, value, unit));
            Add("tracks_started", summary.Started); Add("request_calls", summary.RequestCalls); Add("direct_calls", summary.DirectCalls);
            Add("confirmed", summary.Confirmed); Add("rejected", summary.Rejected); Add("censored", summary.Censored); Add("timed_out", summary.TimedOut);
            Add("duplicates", summary.Duplicates); Add("unmatched", summary.Unmatched); Add("capacity_skips", summary.CapacitySkipped); Add("pending", summary.Pending);
            Add("ambiguous_confirmed", summary.AmbiguousConfirmed);
            Add("ownership_requests", summary.OwnershipRequests); Add("request_completed", summary.RequestCompleted); Add("direct_completed", summary.DirectCompleted);
            Add("ownership_completed", summary.OwnershipCompleted);
            Add("request_wait_sum", summary.RequestSumMs, "ms"); Add("request_wait_max", summary.RequestMaxMs, "ms");
            Add("direct_wait_sum", summary.DirectSumMs, "ms"); Add("direct_wait_max", summary.DirectMaxMs, "ms");
            Add("ownership_wait_sum", summary.OwnershipSumMs, "ms"); Add("ownership_wait_max", summary.OwnershipMaxMs, "ms");
        }
        internal static void Uninstall() { Enabled = false; Reset(); Patches.UnpatchSelf(); Installed = false; Status = "disabled"; }
    }
}
