using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Times each local-player teleport (portals and other TeleportTo callers) by phase: move, destination zone,
    // active area, objects instantiated (IsAreaReady), floor, native end. Readiness is polled only
    // after the move, under the loading screen; it changes nothing. docs/teleport-loading.md.
    internal static class TeleportLoadingTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".TeleportLoading");
        private static readonly TeleportTimeline Timeline = new TeleportTimeline();
        private static AccessTools.FieldRef<Player, bool>? teleporting;
        private static AccessTools.FieldRef<Player, float>? timer;
        private static AccessTools.FieldRef<Player, Vector3>? target;
        private static AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>>? sceneInstances;
        private static ManualLogSource? log;
        private static int lastFrame;
        private static long yieldsAtStart;
        private static int instancesAtStart;
        private static long started, completed, distantCompleted, replaced, failures;
        // The last teleport that ended since the previous sample; null once exported.
        private static Completed? pendingExport;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; set; }
        internal static string Status { get; private set; } = "disabled";
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private sealed class Completed
        {
            internal double TotalMs, MovedMs, ZoneMs, ActiveAreaMs, AreaReadyMs, FloorMs, ReadyWaitMs, FrameMaxMs, DistanceM;
            internal long Frames, BudgetYields;
            internal int InstancesDelta;
            internal bool Distant, Floor;
        }

        internal static void Install(ManualLogSource logger)
        {
            log = logger;
            try
            {
                teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");
                timer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
                target = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
                sceneInstances = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
                var teleportTo = AccessTools.DeclaredMethod(typeof(Player), "TeleportTo", new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
                var update = AccessTools.DeclaredMethod(typeof(Player), "UpdateTeleport", new[] { typeof(float) });
                if (teleportTo == null || teleportTo.ReturnType != typeof(bool) || update == null || update.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported Player teleport signatures.");
                Patches.Patch(teleportTo, postfix: new HarmonyMethod(typeof(TeleportLoadingTelemetry), nameof(AfterTeleportTo)));
                Patches.Patch(update, postfix: new HarmonyMethod(typeof(TeleportLoadingTelemetry), nameof(AfterUpdateTeleport)));
                Installed = true;
                Status = "installed";
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                Installed = Enabled = false;
                Status = "unavailable";
                logger.LogWarning("Teleport loading telemetry unavailable: " + exception.GetType().Name);
            }
        }

        // Accepted only: TeleportTo returns true on the owner once m_teleporting is set.
        private static void AfterTeleportTo(Player __instance, bool __result, Vector3 pos, bool distantTeleport)
        {
            if (!Enabled || !__result || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            try
            {
                if (Timeline.Active) replaced++;
                started++;
                Timeline.Begin(Now, distantTeleport, Vector3.Distance(__instance.transform.position, pos));
                yieldsAtStart = ObjectCreationBudget.Yields;
                instancesAtStart = InstanceCount();
                lastFrame = Time.frameCount;
            }
            catch { failures++; }
        }

        private static void AfterUpdateTeleport(Player __instance)
        {
            if (!Enabled || !Timeline.Active || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            try
            {
                double now = Now;
                if (Time.frameCount != lastFrame)
                {
                    lastFrame = Time.frameCount;
                    Timeline.Frame(Time.unscaledDeltaTime * 1000.0);
                }
                if (!teleporting!(__instance)) { Finish(now); return; }
                if (timer!(__instance) <= AssetUnloadPolicy.TeleportMoveSeconds) return;
                Vector3 destination = target!(__instance);
                Timeline.Mark(TeleportMilestone.Moved, now);
                ZoneSystem zones = ZoneSystem.instance;
                if (zones == null || ZNetScene.instance == null) return;
                if (Unseen(TeleportMilestone.ZoneLoaded) && zones.IsZoneLoaded(destination)) Timeline.Mark(TeleportMilestone.ZoneLoaded, now);
                if (Unseen(TeleportMilestone.ActiveAreaLoaded) && FastTeleportArrival.ReferenceAtDestination(destination) && zones.IsActiveAreaLoaded())
                    Timeline.Mark(TeleportMilestone.ActiveAreaLoaded, now);
                if (Unseen(TeleportMilestone.AreaReady) && ZNetScene.instance.IsAreaReady(destination)) Timeline.Mark(TeleportMilestone.AreaReady, now);
                if (Unseen(TeleportMilestone.FloorFound) && zones.FindFloor(destination, out _)) Timeline.Mark(TeleportMilestone.FloorFound, now);
            }
            catch { failures++; }
        }

        private static bool Unseen(TeleportMilestone milestone) => double.IsNaN(Timeline.SinceStartMs(milestone));

        private static void Finish(double now)
        {
            Timeline.End(now, TeleportOutcome.Arrived);
            completed++;
            if (Timeline.Distant) distantCompleted++;
            pendingExport = new Completed
            {
                TotalMs = Timeline.TotalMs,
                MovedMs = Timeline.SinceStartMs(TeleportMilestone.Moved),
                ZoneMs = Timeline.SinceStartMs(TeleportMilestone.ZoneLoaded),
                ActiveAreaMs = Timeline.SinceStartMs(TeleportMilestone.ActiveAreaLoaded),
                AreaReadyMs = Timeline.SinceStartMs(TeleportMilestone.AreaReady),
                FloorMs = Timeline.SinceStartMs(TeleportMilestone.FloorFound),
                ReadyWaitMs = Timeline.ReadyWaitMs,
                FrameMaxMs = Timeline.FrameMaxMs,
                Frames = Timeline.Frames,
                DistanceM = Timeline.DistanceMeters,
                BudgetYields = Math.Max(0, ObjectCreationBudget.Yields - yieldsAtStart),
                InstancesDelta = InstanceCount() - instancesAtStart,
                Distant = Timeline.Distant,
                Floor = !double.IsNaN(Timeline.SinceStartMs(TeleportMilestone.FloorFound)),
            };
            if (log != null && Timeline.Distant)
                log.LogInfo("Teleport loading: " + Math.Round(Timeline.TotalMs) + " ms total, area ready at " + Round(pendingExport.AreaReadyMs)
                    + " ms, floor at " + Round(pendingExport.FloorMs) + " ms, waited " + Round(pendingExport.ReadyWaitMs) + " ms after readiness.");
        }

        private static string Round(double value) => double.IsNaN(value) ? "never" : Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static int InstanceCount()
        {
            ZNetScene scene = ZNetScene.instance;
            return scene == null || sceneInstances == null ? 0 : sceneInstances(scene).Count;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("teleport_loading_status", Status));
            if (!Installed) return;
            gauges.Add(new NumberValue("teleport_started", Take(ref started), "teleports"));
            gauges.Add(new NumberValue("teleport_completed", Take(ref completed), "teleports"));
            gauges.Add(new NumberValue("teleport_distant_completed", Take(ref distantCompleted), "teleports"));
            gauges.Add(new NumberValue("teleport_replaced_incomplete", Take(ref replaced), "teleports"));
            gauges.Add(new NumberValue("teleport_probe_failures", Take(ref failures), "calls"));
            gauges.Add(new NumberValue("teleport_active", Timeline.Active ? 1 : 0, "flag"));
            var last = pendingExport;
            pendingExport = null;
            if (last == null) return;
            labels.Add(new TextValue("teleport_kind", last.Distant ? "distant" : "short"));
            labels.Add(new TextValue("teleport_end", last.Floor ? "floor_found" : "no_floor"));
            Add(gauges, "teleport_total_ms", last.TotalMs, "ms");
            Add(gauges, "teleport_moved_ms", last.MovedMs, "ms");
            Add(gauges, "teleport_zone_loaded_ms", last.ZoneMs, "ms");
            Add(gauges, "teleport_active_area_ms", last.ActiveAreaMs, "ms");
            Add(gauges, "teleport_area_ready_ms", last.AreaReadyMs, "ms");
            Add(gauges, "teleport_floor_ms", last.FloorMs, "ms");
            Add(gauges, "teleport_ready_wait_ms", last.ReadyWaitMs, "ms");
            Add(gauges, "teleport_frame_max_ms", last.FrameMaxMs, "ms");
            Add(gauges, "teleport_distance_m", last.DistanceM, "m");
            gauges.Add(new NumberValue("teleport_frames", last.Frames, "frames"));
            gauges.Add(new NumberValue("teleport_budget_yields", last.BudgetYields, "loop_exits"));
            gauges.Add(new NumberValue("teleport_instances_delta", last.InstancesDelta, "objects"));
        }

        // A milestone never reached is omitted rather than exported as zero.
        private static void Add(List<NumberValue> gauges, string name, double value, string unit)
        {
            if (!double.IsNaN(value)) gauges.Add(new NumberValue(name, Math.Round(value, 1), unit));
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset()
        {
            started = completed = distantCompleted = replaced = failures = 0;
            pendingExport = null;
        }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            Installed = Enabled = false;
            Status = "disabled";
            Reset();
        }
    }
}
