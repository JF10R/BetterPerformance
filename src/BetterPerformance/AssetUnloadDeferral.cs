using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Game.Start schedules CollectResourcesCheckPeriodic every 3600 s; it runs
    // Resources.UnloadUnusedAssets mid-play (167-332 ms loop gaps on 2026-09-23). A client leaves
    // it to the native sleep/respawn/idle-pause checks and to distant teleports, a dedicated server
    // to its next empty moment, both bounded by MaxDeferMinutes. Nothing is dropped; docs/asset-unload-deferral.md.
    internal static class AssetUnloadDeferral
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".AssetUnloadDeferral");
        private const float PumpSeconds = 5;
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<int>? maxDeferMinutes;
        private static ConfigEntry<bool>? duringTeleport;
        private static AccessTools.FieldRef<Player, bool>? teleporting, distantTeleport;
        private static AccessTools.FieldRef<Player, float>? teleportTimer;
        private static AccessTools.FieldRef<Player, Vector3>? teleportTarget;
        // Set once the current teleport has offered its unload; cleared when it ends.
        private static bool teleportOffered;
        private static AccessTools.FieldRef<Game, DateTime>? lastUnload;
        private static ManualLogSource? log;
        private static bool pending;
        private static float nextPump;
        // Server: when peers last went from none to some; the deferral cap counts from here.
        private static DateTime? playStarted;
        private static long deferred, capped, idleRuns, nativeRuns, teleportRuns;
        private static double teleportMsMax;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Memory", "DeferHourlyAssetUnloadEnabled", false,
                "Move the game's hourly unused-asset unload (a 150-350 ms freeze) out of play: a client leaves it to its native sleep, " +
                "respawn and idle-pause checks, a dedicated server runs it once no player is connected. Requires restart.");
            maxDeferMinutes = config.Bind("Memory", "MaxDeferMinutes", 120, new ConfigDescription(
                "Longest deferral before the hourly check runs the unload anyway, counted from the last unload or, on a server, from when players arrived if later.",
                new AcceptableValueRange<int>(60, 720)));
            duringTeleport = config.Bind("Memory", "UnloadDuringTeleport", true,
                "Client: once a distant teleport has moved you and the destination is ready, run the game's own 20-minute unload check under the " +
                "loading screen, as it already does when you sleep or respawn. Needs DeferHourlyAssetUnloadEnabled. Requires restart.");
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
                bool teleport = duringTeleport.Value && InstallTeleport();
                Installed = true;
                Status = "installed";
                logger.LogInfo("Hourly asset unload deferral installed; cap " + maxDeferMinutes.Value + " min"
                    + (teleport ? "; distant teleports offer the native unload check." : "."));
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

        // Optional: a failure here keeps the deferral itself, without the teleport opportunity.
        private static bool InstallTeleport()
        {
            try
            {
                if (AccessTools.DeclaredMethod(typeof(Game), "CollectResourcesCheck", Type.EmptyTypes) is not MethodInfo check || !check.IsPublic || check.IsStatic)
                    throw new InvalidOperationException("Game.CollectResourcesCheck() is missing.");
                var update = AccessTools.DeclaredMethod(typeof(Player), "UpdateTeleport", new[] { typeof(float) })
                    ?? throw new InvalidOperationException("Player.UpdateTeleport(float) is missing.");
                teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");
                distantTeleport = AccessTools.FieldRefAccess<Player, bool>("m_distantTeleport");
                teleportTimer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
                teleportTarget = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
                // Prefix: the unload runs before the native call can end the teleport in this frame,
                // so the freeze lands on a frame whose last presented image is still the loading screen.
                Patches.Patch(update, prefix: new HarmonyMethod(typeof(AssetUnloadDeferral), nameof(BeforeUpdateTeleport)));
                return true;
            }
            catch (Exception exception)
            {
                teleporting = distantTeleport = null;
                teleportTimer = null;
                teleportTarget = null;
                log?.LogWarning("Unload during teleport unavailable: " + exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        private static void BeforeUpdateTeleport(Player __instance)
        {
            if (!Installed || teleporting == null || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            try
            {
                if (!teleporting(__instance)) { teleportOffered = false; return; }
                if (teleportOffered || !distantTeleport!(__instance) || teleportTimer!(__instance) <= AssetUnloadPolicy.TeleportMoveSeconds) return;
                Game game = Game.instance;
                ZNetScene scene = ZNetScene.instance;
                if (game == null || scene == null) return;
                bool ready = scene.IsAreaReady(teleportTarget!(__instance));
                if (!AssetUnloadPolicy.OfferDuringTeleport(true, teleportTimer(__instance), ready, teleportOffered)) return;
                teleportOffered = true;
                DateTime before = lastUnload!(game);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                game.CollectResourcesCheck();
                if (lastUnload(game) != before)
                {
                    teleportRuns++;
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms > teleportMsMax) teleportMsMax = ms;
                }
            }
            catch (Exception exception)
            {
                teleportOffered = true;
                log?.LogWarning("Unload during teleport failed: " + exception.GetType().Name);
            }
        }

        private static double SinceLastUnload(Game game) => (DateTime.Now - lastUnload!(game)).TotalSeconds;

        private static bool Dedicated(ZNet net) => !ReferenceEquals(net, null) && net.IsDedicated();

        private static double SincePlayStarted(bool dedicated) =>
            dedicated && playStarted is DateTime started ? (DateTime.Now - started).TotalSeconds : double.PositiveInfinity;

        // Any failure returns true: the native check then runs exactly as vanilla.
        private static bool BeforePeriodic(Game __instance)
        {
            if (!Installed || __instance == null) return true;
            try
            {
                ZNet net = ZNet.instance;
                bool dedicated = Dedicated(net);
                int peers = ReferenceEquals(net, null) ? 0 : net.GetPeers().Count;
                switch (AssetUnloadPolicy.Periodic(SinceLastUnload(__instance), SincePlayStarted(dedicated), maxDeferMinutes!.Value * 60.0, dedicated, peers))
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
            if (!Installed) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextPump) return;
            nextPump = now + PumpSeconds;
            try
            {
                Game game = Game.instance;
                ZNet net = ZNet.instance;
                if (game == null || !Dedicated(net)) { pending = false; playStarted = null; return; }
                int peers = net.GetPeers().Count;
                if (peers == 0) playStarted = null;
                else if (playStarted == null) playStarted = DateTime.Now;
                if (!pending) return;
                if (!AssetUnloadPolicy.RunDeferredOnServer(pending, SinceLastUnload(game), peers))
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
            gauges.Add(new NumberValue("asset_unload_teleport_runs", Take(ref teleportRuns), "unloads"));
            gauges.Add(new NumberValue("asset_unload_teleport_ms_max", Math.Round(teleportMsMax, 1), "ms"));
            teleportMsMax = 0;
            gauges.Add(new NumberValue("asset_unload_pending", pending ? 1 : 0, "flag"));
            Game game = Game.instance;
            if (game != null && lastUnload != null)
                gauges.Add(new NumberValue("asset_unload_since_last_s", Math.Round(SinceLastUnload(game), 1), "s"));
            if (playStarted is DateTime started)
                gauges.Add(new NumberValue("asset_unload_since_play_s", Math.Round((DateTime.Now - started).TotalSeconds, 1), "s"));
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset()
        {
            deferred = capped = idleRuns = nativeRuns = teleportRuns = 0;
            teleportMsMax = 0;
        }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            Status = "disabled";
            pending = false;
            playStarted = null;
            lastUnload = null;
            option = null;
            maxDeferMinutes = null;
            duringTeleport = null;
            teleporting = distantTeleport = null;
            teleportTimer = null;
            teleportTarget = null;
            teleportOffered = false;
            Reset();
        }
    }
}
