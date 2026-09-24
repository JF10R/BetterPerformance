using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Game.Start schedules CollectResourcesCheckPeriodic every 3600 s; it runs
    // Resources.UnloadUnusedAssets mid-play (167-332 ms loop gaps on 2026-09-23). A client leaves
    // it to the native sleep/respawn/idle-pause checks, a dedicated server to its next empty moment,
    // both bounded by MaxDeferMinutes. Nothing is dropped; docs/asset-unload-deferral.md.
    internal static class AssetUnloadDeferral
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".AssetUnloadDeferral");
        private const float PumpSeconds = 5;
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<int>? maxDeferMinutes;
        private static AccessTools.FieldRef<Game, DateTime>? lastUnload;
        private static ManualLogSource? log;
        private static bool pending;
        private static float nextPump;
        private static long deferred, capped, idleRuns, nativeRuns;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Memory", "DeferHourlyAssetUnloadEnabled", false,
                "Move the game's hourly unused-asset unload (a 150-350 ms freeze) out of play: a client leaves it to its native sleep, " +
                "respawn and idle-pause checks, a dedicated server runs it once no player is connected. Requires restart.");
            maxDeferMinutes = config.Bind("Memory", "MaxDeferMinutes", 120, new ConfigDescription(
                "Longest time since the last unload before the hourly check runs it anyway.",
                new AcceptableValueRange<int>(60, 720)));
            log = logger;
            if (!option.Value) { Status = "disabled"; return; }
            try
            {
                MethodInfo periodic = AccessTools.DeclaredMethod(typeof(Game), "CollectResourcesCheckPeriodic", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("Game.CollectResourcesCheckPeriodic() is missing.");
                if (periodic.IsStatic || periodic.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported Game.CollectResourcesCheckPeriodic signature.");
                if (AccessTools.DeclaredMethod(typeof(Game), "CollectResources", new[] { typeof(bool) }) is not MethodInfo collect ||
                    collect.IsStatic || !collect.IsPublic)
                    throw new InvalidOperationException("Game.CollectResources(bool) is missing.");
                FieldInfo field = AccessTools.DeclaredField(typeof(Game), "m_lastCollectResources")
                    ?? throw new InvalidOperationException("Game.m_lastCollectResources is missing.");
                if (field.IsStatic || field.FieldType != typeof(DateTime))
                    throw new InvalidOperationException("Game.m_lastCollectResources is not an instance DateTime.");
                lastUnload = AccessTools.FieldRefAccess<Game, DateTime>(field);
                Patches.Patch(periodic, prefix: new HarmonyMethod(typeof(AssetUnloadDeferral), nameof(BeforePeriodic)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Hourly asset unload deferral installed; cap " + maxDeferMinutes.Value + " min.");
            }
            catch (Exception exception)
            {
                try { Patches.UnpatchSelf(); } catch { }
                Installed = false;
                lastUnload = null;
                Status = "unavailable";
                logger.LogWarning("Hourly asset unload deferral unavailable; native unload retained: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static double SinceLastUnload(Game game) => (DateTime.Now - lastUnload!(game)).TotalSeconds;

        private static bool Dedicated(ZNet net) => !ReferenceEquals(net, null) && net.IsDedicated();

        // Any failure returns true: the native check then runs exactly as vanilla.
        private static bool BeforePeriodic(Game __instance)
        {
            if (!Installed || __instance == null) return true;
            try
            {
                ZNet net = ZNet.instance;
                bool dedicated = Dedicated(net);
                int peers = ReferenceEquals(net, null) ? 0 : net.GetPeers().Count;
                switch (AssetUnloadPolicy.Periodic(SinceLastUnload(__instance), maxDeferMinutes!.Value * 60.0, dedicated, peers))
                {
                    case AssetUnloadDecision.Defer:
                        deferred++;
                        pending = dedicated;
                        return false;
                    case AssetUnloadDecision.RunCapped:
                        capped++;
                        pending = false;
                        return true;
                    case AssetUnloadDecision.RunNative:
                        nativeRuns++;
                        pending = false;
                        return true;
                    default:
                        return true;
                }
            }
            catch { return true; }
        }

        // Main thread, from Plugin.Update: a deferred server unload runs once nobody is connected.
        internal static void Pump()
        {
            if (!pending) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextPump) return;
            nextPump = now + PumpSeconds;
            try
            {
                Game game = Game.instance;
                ZNet net = ZNet.instance;
                if (game == null || !Dedicated(net)) { pending = false; return; }
                if (!AssetUnloadPolicy.RunDeferredOnServer(pending, SinceLastUnload(game), net.GetPeers().Count))
                {
                    if (SinceLastUnload(game) <= AssetUnloadPolicy.NativePeriodSeconds) pending = false;
                    return;
                }
                pending = false;
                idleRuns++;
                log?.LogInfo("Server empty: running the deferred unused-asset unload.");
                game.CollectResources();
            }
            catch (Exception exception)
            {
                pending = false;
                log?.LogWarning("Deferred asset unload failed: " + exception.GetType().Name);
            }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("asset_unload_status", Status));
            if (!Installed) return;
            gauges.Add(new NumberValue("asset_unload_deferred", Take(ref deferred), "calls"));
            gauges.Add(new NumberValue("asset_unload_capped", Take(ref capped), "unloads"));
            gauges.Add(new NumberValue("asset_unload_idle_runs", Take(ref idleRuns), "unloads"));
            gauges.Add(new NumberValue("asset_unload_native_runs", Take(ref nativeRuns), "unloads"));
            gauges.Add(new NumberValue("asset_unload_pending", pending ? 1 : 0, "flag"));
            Game game = Game.instance;
            if (game != null && lastUnload != null)
                gauges.Add(new NumberValue("asset_unload_since_last_s", Math.Round(SinceLastUnload(game), 1), "s"));
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset() => deferred = capped = idleRuns = nativeRuns = 0;

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            Status = "disabled";
            pending = false;
            lastUnload = null;
            option = null;
            maxDeferMinutes = null;
            Reset();
        }
    }
}
