using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using UnityEngine;
using UnityEngine.LowLevel;

namespace BetterPerformance
{
    // Stopwatch stamps in the engine loop, read-only: around TimeUpdate.WaitForLastPresentationAndUpdateTime,
    // where Unity sleeps to the target frame rate (and waits for the previous present), and around every
    // FixedUpdate pass. Gives per-frame work against the 60/120/144 Hz budgets and the cost of the fixed
    // steps a slow frame replays. The profiler markers could not: WaitForTargetFPS misses the sleep and
    // the fixed-phase markers are absent from the shipped player (isolated run, 2026-09-26). docs/engine-telemetry.md.
    internal static class FramePacingTelemetry
    {
        private sealed class BeforePacing { }
        private sealed class AfterPacing { }
        private sealed class FixedStart { }
        private sealed class FixedEnd { }

        // A maximumDeltaTime of 0.1 s is the cap whose effect is projected.
        private const double ProjectedCapSeconds = 0.1;
        private static readonly FrameBusyWindow Busy = new FrameBusyWindow();
        private static readonly FixedCatchupWindow Catchup = new FixedCatchupWindow();
        private static long beforeWait, afterWait, fixedStart;
        private static long frameSteps;
        private static double frameFixedMs;
        private static bool pacingInstalled, fixedInstalled;

        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            if (!config.Bind("Diagnostics", "FramePacingEnabled", true,
                "Stamp the engine loop around its frame-rate wait and each fixed step to measure per-frame work and the catch-up cost. " +
                "Read-only; no setting is changed. Requires restart.").Value) return;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                pacingInstalled = Wrap(ref root, typeof(UnityEngine.PlayerLoop.TimeUpdate.WaitForLastPresentationAndUpdateTime),
                    typeof(BeforePacing), OnBeforePacing, typeof(AfterPacing), OnAfterPacing);
                fixedInstalled = WrapPhase(ref root, typeof(UnityEngine.PlayerLoop.FixedUpdate), typeof(FixedStart), OnFixedStart, typeof(FixedEnd), OnFixedEnd);
                if (pacingInstalled || fixedInstalled) PlayerLoop.SetPlayerLoop(root);
                Status = pacingInstalled && fixedInstalled ? "installed" : pacingInstalled ? "installed_without_fixed" : fixedInstalled ? "installed_without_pacing" : "unavailable";
            }
            catch (Exception exception)
            {
                pacingInstalled = fixedInstalled = false;
                Status = "unavailable";
                logger.LogWarning("Frame pacing telemetry unavailable: " + exception.GetType().Name);
            }
        }

        // Inserts a stamp right before and right after one system, wherever it sits.
        private static bool Wrap(ref PlayerLoopSystem root, Type target, Type beforeType, PlayerLoopSystem.UpdateFunction before,
            Type afterType, PlayerLoopSystem.UpdateFunction after)
        {
            for (int i = 0; root.subSystemList != null && i < root.subSystemList.Length; i++)
            {
                PlayerLoopSystem phase = root.subSystemList[i];
                if (phase.subSystemList == null) continue;
                int index = Array.FindIndex(phase.subSystemList, system => system.type == target);
                if (index < 0) continue;
                var list = new List<PlayerLoopSystem>(phase.subSystemList);
                list.Insert(index + 1, new PlayerLoopSystem { type = afterType, updateDelegate = after });
                list.Insert(index, new PlayerLoopSystem { type = beforeType, updateDelegate = before });
                phase.subSystemList = list.ToArray();
                root.subSystemList[i] = phase;
                return true;
            }
            return false;
        }

        // Stamps at the head and tail of a whole phase: they run once per pass, so once per fixed step.
        private static bool WrapPhase(ref PlayerLoopSystem root, Type target, Type headType, PlayerLoopSystem.UpdateFunction head,
            Type tailType, PlayerLoopSystem.UpdateFunction tail)
        {
            for (int i = 0; root.subSystemList != null && i < root.subSystemList.Length; i++)
            {
                PlayerLoopSystem phase = root.subSystemList[i];
                if (phase.type != target) continue;
                var list = new List<PlayerLoopSystem>(phase.subSystemList ?? Array.Empty<PlayerLoopSystem>());
                list.Insert(0, new PlayerLoopSystem { type = headType, updateDelegate = head });
                list.Add(new PlayerLoopSystem { type = tailType, updateDelegate = tail });
                phase.subSystemList = list.ToArray();
                root.subSystemList[i] = phase;
                return true;
            }
            return false;
        }

        // End of the previous frame's work: close that frame.
        private static void OnBeforePacing()
        {
            long now = Stopwatch.GetTimestamp();
            if (frameSteps > 0) Catchup.Note(frameSteps, frameFixedMs, FixedCatchupWindow.CapSteps(ProjectedCapSeconds, Time.fixedDeltaTime));
            frameSteps = 0;
            frameFixedMs = 0;
            beforeWait = now;
        }

        // Start of this frame's work: the previous frame lasted from the last start to now.
        private static void OnAfterPacing()
        {
            long now = Stopwatch.GetTimestamp();
            if (afterWait != 0 && beforeWait != 0 && beforeWait >= afterWait)
                Busy.Note(Ms(now - afterWait), Ms(now - beforeWait));
            afterWait = now;
        }

        private static void OnFixedStart() => fixedStart = Stopwatch.GetTimestamp();

        private static void OnFixedEnd()
        {
            if (fixedStart == 0) return;
            frameSteps++;
            frameFixedMs += Ms(Stopwatch.GetTimestamp() - fixedStart);
            fixedStart = 0;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("frame_pacing_status", Status));
            if (pacingInstalled)
            {
                FrameBusySnapshot busy = Busy.Drain();
                gauges.Add(new NumberValue("frame_busy_frames", busy.Frames, "frames"));
                gauges.Add(new NumberValue("frame_busy_ms_sum", Math.Round(busy.BusyMsSum, 2), "ms"));
                gauges.Add(new NumberValue("frame_busy_ms_max", Math.Round(busy.BusyMsMax, 2), "ms"));
                gauges.Add(new NumberValue("frame_wait_ms_sum", Math.Round(busy.WaitMsSum, 2), "ms"));
                gauges.Add(new NumberValue("frame_busy_over_60hz", busy.Over60Hz, "frames"));
                gauges.Add(new NumberValue("frame_busy_over_120hz", busy.Over120Hz, "frames"));
                gauges.Add(new NumberValue("frame_busy_over_144hz", busy.Over144Hz, "frames"));
            }
            if (fixedInstalled)
            {
                FixedCatchupSnapshot c = Catchup.Drain();
                gauges.Add(new NumberValue("fixed_catchup_paired_frames", c.PairedFrames, "frames"));
                gauges.Add(new NumberValue("fixed_catchup_paired_steps", c.PairedSteps, "steps"));
                gauges.Add(new NumberValue("fixed_catchup_frames", c.CatchupFrames, "frames"));
                gauges.Add(new NumberValue("fixed_catchup_extra_steps", c.ExtraSteps, "steps"));
                gauges.Add(new NumberValue("fixed_phase_ms_sum", Math.Round(c.FixedMsSum, 2), "ms"));
                gauges.Add(new NumberValue("fixed_phase_ms_max", Math.Round(c.FixedMsMax, 2), "ms"));
                gauges.Add(new NumberValue("fixed_catchup_ms_sum", Math.Round(c.CatchupFixedMsSum, 2), "ms"));
                gauges.Add(new NumberValue("fixed_catchup_steps_beyond_100ms_cap", c.StepsBeyondCap, "steps"));
                gauges.Add(new NumberValue("fixed_catchup_projected_saved_ms", Math.Round(c.ProjectedSavedMs, 2), "ms"));
            }
        }

        internal static void Reset()
        {
            Busy.Drain();
            Catchup.Drain();
        }

        internal static void Uninstall()
        {
            if (!pacingInstalled && !fixedInstalled) return;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                var ours = new HashSet<Type> { typeof(BeforePacing), typeof(AfterPacing), typeof(FixedStart), typeof(FixedEnd) };
                for (int i = 0; root.subSystemList != null && i < root.subSystemList.Length; i++)
                {
                    PlayerLoopSystem phase = root.subSystemList[i];
                    if (phase.subSystemList == null) continue;
                    var kept = new List<PlayerLoopSystem>(phase.subSystemList);
                    if (kept.RemoveAll(system => ours.Contains(system.type)) == 0) continue;
                    phase.subSystemList = kept.ToArray();
                    root.subSystemList[i] = phase;
                }
                PlayerLoop.SetPlayerLoop(root);
            }
            catch { }
            pacingInstalled = fixedInstalled = false;
            Status = "disabled";
        }
    }
}
