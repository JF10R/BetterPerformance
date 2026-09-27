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
    // after the move, under the loading screen; it changes nothing. After a distant arrival, the first
    // 10 s of play are watched too (slow frames, objects still pending). docs/teleport-loading.md.
    internal static class TeleportLoadingTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".TeleportLoading");
        private static readonly TeleportTimeline Timeline = new TeleportTimeline();
        private static readonly PostArrivalWindow After = new PostArrivalWindow();
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
        // The first seconds of play after the last distant arrival; exported once closed.
        private static PostArrivalSnapshot? pendingAfter;
        private static int instancesAtArrival, createdAfter;
        private static double lastPendingCount = double.NaN;

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
                return;
            }
            // Separate: losing the pending-object count costs that count, never the teleport timeline.
            try
            {
                var create = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
                if (create == null || create.ReturnType != typeof(void)) throw new InvalidOperationException("ZNetScene.CreateObjects is missing.");
                Patches.Patch(create, prefix: new HarmonyMethod(typeof(TeleportLoadingTelemetry), nameof(BeforeCreateObjects)));
            }
            catch (Exception exception)
            {
                Status = "installed_without_pending_objects";
                logger.LogWarning("Teleport arrival pending-object count unavailable: " + exception.GetType().Name);
            }
        }

        // Accepted only: TeleportTo returns true on the owner once m_teleporting is set.
        private static void AfterTeleportTo(Player __instance, bool __result, Vector3 pos, bool distantTeleport)
        {
            if (!Enabled || !__result || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            try
            {
                if (Timeline.Active) replaced++;
                if (After.Active) { After.Cut(Now); CloseAfter(); }
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
                if (!teleporting!(__instance)) { MarkFinalFrame(__instance, now); Finish(now); return; }
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

        // The native end (or FastTeleportArrival) can clear m_teleporting in the very call where the area
        // became ready, before this postfix polled it: the readiness is marked at the end then.
        private static void MarkFinalFrame(Player player, double now)
        {
            if (Unseen(TeleportMilestone.Moved) || ZNetScene.instance == null || ZoneSystem.instance == null) return;
            Vector3 destination = target!(player);
            if (Unseen(TeleportMilestone.AreaReady) && ZNetScene.instance.IsAreaReady(destination)) Timeline.Mark(TeleportMilestone.AreaReady, now);
            if (Unseen(TeleportMilestone.FloorFound) && ZoneSystem.instance.FindFloor(destination, out _)) Timeline.Mark(TeleportMilestone.FloorFound, now);
        }

        // Main thread, every frame (Plugin.Update): only a live post-arrival window costs anything.
        internal static void NoteFrame()
        {
            if (!Enabled || !After.Active) return;
            try
            {
                EngineTelemetry.LastFrame(out double mainMs, out double gpuMs);
                if (After.Frame(Now, Time.unscaledDeltaTime * 1000.0, mainMs, gpuMs)) CloseAfter();
            }
            catch { failures++; }
        }

        // Up to four counts a second while the window is open: objects of the scene's near and
        // distant lists that are not created yet.
        private static void BeforeCreateObjects(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            if (!Enabled || !After.Active) return;
            try
            {
                double now = Now;
                if (!double.IsNaN(lastPendingCount) && now - lastPendingCount < 0.25 && now >= lastPendingCount) return;
                lastPendingCount = now;
                After.Pending(now, Uncreated(currentNearObjects), Uncreated(currentDistantObjects));
            }
            catch { failures++; }
        }

        private static int Uncreated(List<ZDO> list)
        {
            if (list == null) return 0;
            int count = 0;
            foreach (ZDO zdo in list) if (zdo != null && !zdo.Created) count++;
            return count;
        }

        private static void CloseAfter()
        {
            PostArrivalSnapshot result = After.Result;
            createdAfter = InstanceCount() - instancesAtArrival;
            pendingAfter = result;
            if (log == null) return;
            log.LogInfo("Teleport after arrival: " + Math.Round(result.DurationMs / 1000, 1) + " s, " + result.SlowFrames + " frames over 50 ms (max "
                + Math.Round(result.MaxFrameMs) + " ms; GPU " + result.SlowGpuBound + ", main thread " + result.SlowCpuBound + "), near objects pending at arrival "
                + (result.NearPendingAtArrival < 0 ? "unknown" : result.NearPendingAtArrival.ToString(System.Globalization.CultureInfo.InvariantCulture))
                + ", drained at " + Round(result.NearDrainedMs) + " ms" + (result.Cut ? ", cut by a new teleport." : "."));
        }

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
            if (!Timeline.Distant) return;
            instancesAtArrival = InstanceCount();
            lastPendingCount = double.NaN;
            After.Begin(now);
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
            gauges.Add(new NumberValue("teleport_after_active", After.Active ? 1 : 0, "flag"));
            SampleAfter(gauges, labels);
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

        private static void SampleAfter(List<NumberValue> gauges, List<TextValue> labels)
        {
            if (pendingAfter == null) return;
            PostArrivalSnapshot after = pendingAfter.Value;
            pendingAfter = null;
            labels.Add(new TextValue("teleport_after_end", after.Cut ? "cut_by_teleport" : "window_closed"));
            Add(gauges, "teleport_after_duration_ms", after.DurationMs, "ms");
            gauges.Add(new NumberValue("teleport_after_frames", after.Frames, "frames"));
            gauges.Add(new NumberValue("teleport_after_slow_frames", after.SlowFrames, "frames"));
            gauges.Add(new NumberValue("teleport_after_very_slow_frames", after.VerySlowFrames, "frames"));
            gauges.Add(new NumberValue("teleport_after_slow_gpu", after.SlowGpuBound, "frames"));
            gauges.Add(new NumberValue("teleport_after_slow_main_thread", after.SlowCpuBound, "frames"));
            Add(gauges, "teleport_after_frame_max_ms", after.MaxFrameMs, "ms");
            Add(gauges, "teleport_after_slow_ms_sum", after.SlowFrameMsSum, "ms");
            if (after.NearPendingAtArrival >= 0) gauges.Add(new NumberValue("teleport_after_near_pending", after.NearPendingAtArrival, "objects"));
            if (after.DistantPendingAtArrival >= 0) gauges.Add(new NumberValue("teleport_after_distant_pending", after.DistantPendingAtArrival, "objects"));
            Add(gauges, "teleport_after_near_drained_ms", after.NearDrainedMs, "ms");
            gauges.Add(new NumberValue("teleport_after_instances_delta", createdAfter, "objects"));
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
            pendingAfter = null;
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
