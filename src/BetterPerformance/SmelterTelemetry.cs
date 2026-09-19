using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Smelter.UpdateSmelter replays an absence in one call: it reads elapsed time and the ZDO
    // accumulator, clamps the sum to 3600 simulated seconds, then drains it with no idle-time
    // carry of its own. This module only observes that catch-up size and what lingers in the
    // accumulator afterwards; it patches nothing behavioural and writes no ZDO variable. The
    // catch-up budget that used to bound and bank that drain was removed in 0.4.11: an idle
    // absence vanilla discards was instead banked, so ore added later smelted at up to 8x real
    // time for minutes. See docs/smelter-catchup-budget.md.
    internal static class SmelterTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".SmelterTelemetry");
        private static AccessTools.FieldRef<Smelter, ZNetView>? view;

        private static long catchupMilliseconds, callsOver60s, carriedMilliseconds;
        private static long spawnCalls, removeOreCalls, failures;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ManualLogSource logger)
        {
            try
            {
                MethodInfo? target = AccessTools.DeclaredMethod(typeof(Smelter), "UpdateSmelter", Type.EmptyTypes);
                if (target == null || target.IsStatic || !target.IsPrivate || target.ReturnType != typeof(void) || target.GetParameters().Length != 0)
                { Status = "type_unavailable"; return; }
                MethodInfo? spawn = AccessTools.DeclaredMethod(typeof(Smelter), "Spawn", new[] { typeof(string), typeof(int) });
                MethodInfo? removeOre = AccessTools.DeclaredMethod(typeof(Smelter), "RemoveOneOre", Type.EmptyTypes);
                if (spawn == null || removeOre == null) { Status = "type_unavailable"; return; }
                view = AccessTools.FieldRefAccess<Smelter, ZNetView>("m_nview");
                Patches.Patch(target,
                    prefix: new HarmonyMethod(typeof(SmelterTelemetry), nameof(BeforeUpdate)),
                    postfix: new HarmonyMethod(typeof(SmelterTelemetry), nameof(AfterUpdate)));
                Patches.Patch(spawn, postfix: new HarmonyMethod(typeof(SmelterTelemetry), nameof(AfterSpawn)));
                Patches.Patch(removeOre, postfix: new HarmonyMethod(typeof(SmelterTelemetry), nameof(AfterRemoveOre)));
                Installed = true;
                Status = "installed";
            }
            catch (Exception exception)
            {
                Installed = false;
                Status = "patch_failed";
                Remove(failure => Status = "unpatch_failed:" + failure);
                logger.LogWarning("Smelter telemetry unavailable: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        // Harmony rescans every patched method when it removes one, and a standalone CLR can
        // throw there for an unrelated Unity type. Removal must never escape a caller.
        private static void Remove(Action<string> failed)
        {
            try { Patches.UnpatchSelf(); }
            catch (Exception exception) { failed(exception.GetType().Name); }
        }

        // Reproduces vanilla's own pre-loop arithmetic (elapsed time plus the accTime
        // accumulator, clamped to 3600) without calling the game's GetDeltaTime, which writes
        // s_startTime as a side effect; calling it here would steal time from the real update.
        // Read-only: no ZDO var is ever written by this module.
        private static void BeforeUpdate(Smelter __instance)
        {
            try
            {
                ZNetView? nview = view == null ? null : view(__instance);
                if (nview == null || !nview.IsValid() || !nview.IsOwner() || ZNet.instance == null) return;
                ZDO zdo = nview.GetZDO();
                DateTime time = ZNet.instance.GetTime();
                long startTicks = zdo.GetLong(ZDOVars.s_startTime, time.Ticks);
                double elapsed = (time - new DateTime(startTicks)).TotalSeconds;
                float projected = zdo.GetFloat(ZDOVars.s_accTime) + (float)elapsed;
                projected = projected < 0f ? 0f : projected > 3600f ? 3600f : projected;
                RecordMaximum(ref catchupMilliseconds, (long)(projected * 1000f));
                if (projected >= 60f) Interlocked.Increment(ref callsOver60s);
            }
            catch { Interlocked.Increment(ref failures); }
        }

        private static void AfterUpdate(Smelter __instance)
        {
            try
            {
                ZNetView? nview = view == null ? null : view(__instance);
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
                float carried = nview.GetZDO().GetFloat(ZDOVars.s_accTime);
                if (carried > 0f) RecordMaximum(ref carriedMilliseconds, (long)(carried * 1000f));
            }
            catch { Interlocked.Increment(ref failures); }
        }

        private static void AfterSpawn() => Interlocked.Increment(ref spawnCalls);

        private static void AfterRemoveOre() => Interlocked.Increment(ref removeOreCalls);

        private static void RecordMaximum(ref long target, long value)
        {
            long seen = Interlocked.Read(ref target);
            while (value > seen)
            {
                long previous = Interlocked.CompareExchange(ref target, value, seen);
                if (previous == seen) return;
                seen = previous;
            }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("smelter_catchup_seconds_max", Interlocked.Exchange(ref catchupMilliseconds, 0) / 1000.0, "seconds"));
            gauges.Add(new NumberValue("smelter_catchup_calls_over_60s", Interlocked.Exchange(ref callsOver60s, 0), "calls"));
            gauges.Add(new NumberValue("smelter_accumulator_carried_max", Interlocked.Exchange(ref carriedMilliseconds, 0) / 1000.0, "s"));
            gauges.Add(new NumberValue("smelter_spawn_calls", Interlocked.Exchange(ref spawnCalls, 0), "calls"));
            gauges.Add(new NumberValue("smelter_remove_ore_calls", Interlocked.Exchange(ref removeOreCalls, 0), "calls"));
            gauges.Add(new NumberValue("smelter_probe_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("smelter_telemetry_status", Status));
            labels.Add(new TextValue("smelter_telemetry_scope", "owner_only; read_only; vanilla_catchup_is_one_call"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref catchupMilliseconds, 0);
            Interlocked.Exchange(ref callsOver60s, 0);
            Interlocked.Exchange(ref carriedMilliseconds, 0);
            Interlocked.Exchange(ref spawnCalls, 0);
            Interlocked.Exchange(ref removeOreCalls, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Uninstall()
        {
            Reset();
            Installed = false;
            Status = "disabled";
            Remove(failure => Status = "unpatch_failed:" + failure);
            view = null;
        }
    }
}
