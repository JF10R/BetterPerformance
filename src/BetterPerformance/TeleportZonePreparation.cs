using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Two loading-screen aids for distant teleports (client), both leaving zone construction native:
    // - prefetch: on the accepted TeleportTo, queue the destination zones' terrain on the native
    //   HeightmapBuilder thread, so the 2 s fade overlaps work vanilla only starts after the move;
    // - burst: InitialLoadingOptimization runs extra CreateLocalZones passes while MovedDistant()
    //   holds, where vanilla creates one zone per 0.1 s. docs/teleport-loading.md.
    internal static class TeleportZonePreparation
    {
        // HeightmapBuilder keeps 16 finished builds and evicts the oldest: never queue more.
        internal const int MaxPrefetch = 16;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".TeleportZonePreparation");
        private static ConfigEntry<bool>? prefetchOption, burstOption;
        private static ConfigEntry<float>? burstMilliseconds;
        private static AccessTools.FieldRef<Player, bool>? teleporting, distantTeleport;
        private static AccessTools.FieldRef<Player, float>? teleportTimer;
        private static AccessTools.FieldRef<Player, Vector3>? teleportTarget;
        private static long prefetchTeleports, prefetchRequests, prefetchAlreadyKnown, failures;

        internal static bool PrefetchInstalled { get; private set; }
        internal static bool BurstEnabled { get; private set; }
        internal static float BurstMilliseconds => burstMilliseconds?.Value ?? 16f;
        internal static string Status { get; private set; } = "disabled";

        // Binds before InitialLoadingOptimization.Install, which reads BurstEnabled.
        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            prefetchOption = config.Bind("Teleport", "PrefetchTerrainEnabled", false,
                "Client: when a portal starts, queue the terrain of the zones nearest its destination (at most 16) on the game's own terrain builder, " +
                "during the 2 s fade. Same data the game would build after the move. Requires restart.");
            burstOption = config.Bind("Teleport", "ZoneBurstEnabled", false,
                "Client: behind a portal's loading screen, after the move, create several destination zones per frame instead of one per 0.1 s. " +
                "Same native zone creation, as the initial join does with [InitialLoading] Enabled. Requires restart.");
            burstMilliseconds = config.Bind("Teleport", "ZoneBurstMilliseconds", 16f, new ConfigDescription(
                "Soft per-frame time allowance for extra zone passes under the loading screen.", new AcceptableValueRange<float>(4f, 33f)));
            if (!prefetchOption.Value && !burstOption.Value) return;
            try
            {
                teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");
                distantTeleport = AccessTools.FieldRefAccess<Player, bool>("m_distantTeleport");
                teleportTimer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
                teleportTarget = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
                BurstEnabled = burstOption.Value;
                if (prefetchOption.Value)
                {
                    var teleportTo = AccessTools.DeclaredMethod(typeof(Player), "TeleportTo", new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
                    if (teleportTo == null || teleportTo.ReturnType != typeof(bool)) throw new InvalidOperationException("Player.TeleportTo is missing.");
                    Patches.Patch(teleportTo, postfix: new HarmonyMethod(typeof(TeleportZonePreparation), nameof(AfterTeleportTo)));
                    PrefetchInstalled = true;
                }
                Status = "installed";
                logger.LogInfo("Teleport zone preparation installed: prefetch " + PrefetchInstalled + ", zone burst " + BurstEnabled + " (" + BurstMilliseconds + " ms).");
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                PrefetchInstalled = BurstEnabled = false;
                Status = "unavailable";
                logger.LogWarning("Teleport zone preparation unavailable: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        // A distant teleport past its move (2 s), with ZNet's reference position already at the target:
        // the loading screen is up and the zones being created are the destination's.
        internal static bool MovedDistant(Vector3 referencePoint)
        {
            Player player = Player.m_localPlayer;
            if (player == null || teleporting == null || !teleporting(player) || !distantTeleport!(player)) return false;
            if (teleportTimer!(player) <= AssetUnloadPolicy.TeleportMoveSeconds) return false;
            return Utils.DistanceXZ(referencePoint, teleportTarget!(player)) < 32f;
        }

        private static void AfterTeleportTo(Player __instance, bool __result, Vector3 pos, bool distantTeleport)
        {
            if (!__result || !distantTeleport || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            try
            {
                ZoneSystem zones = ZoneSystem.instance;
                HeightmapBuilder builder = HeightmapBuilder.instance;
                ZNet net = ZNet.instance;
                if (zones == null || builder == null || net == null || zones.m_zonePrefab == null || WorldGenerator.instance == null) return;
                Heightmap heightmap = zones.m_zonePrefab.GetComponentInChildren<Heightmap>();
                if (heightmap == null) return;
                prefetchTeleports++;
                foreach (Vector2s zone in NearestZones(zones, ZoneSystem.GetZone(pos), net.GetSyncedSimulationDistance()))
                {
                    if (zones.IsZoneLoaded(zone)) continue;
                    // The same call SpawnZone makes: it queues the build when neither ready nor queued.
                    if (builder.IsTerrainReady(ZoneSystem.GetZonePos(zone), heightmap.m_width, heightmap.m_scale, heightmap.IsDistantLod, WorldGenerator.instance))
                        prefetchAlreadyKnown++;
                    else prefetchRequests++;
                }
            }
            catch { failures++; }
        }

        // The zones CreateLocalZones would create around the target, nearest first, capped at MaxPrefetch.
        private static IEnumerable<Vector2s> NearestZones(ZoneSystem zones, Vector2s center, SimulationDistance distance)
        {
            int radius = distance.NearSimulationDistance;
            var list = new List<Vector2s>();
            for (int y = center.y - radius; y <= center.y + radius; y++)
                for (int x = center.x - radius; x <= center.x + radius; x++)
                {
                    var zone = new Vector2s(x, y);
                    if (distance.IsClassic || zone == center || zones.ZonesWithinRadius(center, zone, radius)) list.Add(zone);
                }
            list.Sort((a, b) => ((a.x - center.x) * (a.x - center.x) + (a.y - center.y) * (a.y - center.y))
                .CompareTo((b.x - center.x) * (b.x - center.x) + (b.y - center.y) * (b.y - center.y)));
            if (list.Count > MaxPrefetch) list.RemoveRange(MaxPrefetch, list.Count - MaxPrefetch);
            return list;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("teleport_zone_preparation_status", Status));
            if (!PrefetchInstalled) return;
            gauges.Add(new NumberValue("teleport_prefetch_teleports", Take(ref prefetchTeleports), "teleports"));
            gauges.Add(new NumberValue("teleport_prefetch_requests", Take(ref prefetchRequests), "zones"));
            gauges.Add(new NumberValue("teleport_prefetch_already_known", Take(ref prefetchAlreadyKnown), "zones"));
            gauges.Add(new NumberValue("teleport_prefetch_failures", Take(ref failures), "calls"));
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset() => prefetchTeleports = prefetchRequests = prefetchAlreadyKnown = failures = 0;

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            PrefetchInstalled = BurstEnabled = false;
            Status = "disabled";
            teleporting = distantTeleport = null;
            teleportTimer = null;
            teleportTarget = null;
            Reset();
        }
    }
}
