using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // A distant teleport holds its loading screen until 8 s have passed, even when the destination
    // loaded in 3. This ends it once everything near the player is loaded (TeleportArrivalPolicy),
    // by advancing m_teleportTimer past 8 s: vanilla then applies its own IsAreaReady and FindFloor
    // in the same call. It never holds a teleport longer than vanilla. docs/teleport-loading.md.
    internal static class FastTeleportArrival
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".FastTeleportArrival");
        private const double CheckIntervalSeconds = 0.1;
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<float>? minimumSeconds, settleSeconds, nearRadius;
        private static AccessTools.FieldRef<Player, bool>? teleporting, distantTeleport;
        private static AccessTools.FieldRef<Player, float>? teleportTimer;
        private static AccessTools.FieldRef<Player, Vector3>? teleportTarget;
        private static FieldInfo? grassPatches, grassQuality;
        private static AccessTools.FieldRef<ClutterSystem, bool>? grassForceRebuild;
        private static bool grassRequested;
        private static ManualLogSource? log;
        private static readonly TeleportArrivalPolicy.Settle Settle = new TeleportArrivalPolicy.Settle();
        private static readonly List<ZDO> AreaObjects = new List<ZDO>();
        private static readonly long[] BlockerCounts = new long[Enum.GetValues(typeof(ArrivalBlocker)).Length];
        // Wall time each blocker held a distant teleport, applied or not: what to optimize next.
        private static readonly double[] BlockerWaitMs = new double[BlockerCounts.Length];
        private static double lastCheck;
        private static bool active;
        private static Player? activePlayer;
        private static double nextCheck;
        private static ArrivalBlocker lastBlocker;
        private static long applied, nativeFloor, failures, grassPrebuilt;
        private static double savedMsMax, savedMsSum;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            option = config.Bind("Teleport", "FastArrivalEnabled", false,
                "Client: end a distant teleport (portal) before the fixed 8 s once everything near the player is loaded: zones, received objects, " +
                "ground, terrain rebuilds, grass, dungeon rooms, and no new object from the server for SettleSeconds. Never longer than vanilla. Requires restart.");
            minimumSeconds = config.Bind("Teleport", "MinimumSeconds", 3f, new ConfigDescription(
                "Shortest teleport, from entering the portal. Vanilla moves the player at 2 s and ends at 8 s at the earliest.", new AcceptableValueRange<float>(2.5f, 8f)));
            settleSeconds = config.Bind("Teleport", "SettleSeconds", 0.75f, new ConfigDescription(
                "How long the set of objects the server sent around the destination must stay unchanged.", new AcceptableValueRange<float>(0.25f, 3f)));
            nearRadius = config.Bind("Teleport", "NearRadius", 80f, new ConfigDescription(
                "Radius around the destination where no terrain rebuild may still be queued (same default as [Terrain] RebuildCriticalRadius).", new AcceptableValueRange<float>(32f, 160f)));
            if (!option.Value) { Status = "disabled"; return; }
            try
            {
                var update = AccessTools.DeclaredMethod(typeof(Player), "UpdateTeleport", new[] { typeof(float) })
                    ?? throw new InvalidOperationException("Player.UpdateTeleport(float) is missing.");
                teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");
                distantTeleport = AccessTools.FieldRefAccess<Player, bool>("m_distantTeleport");
                teleportTimer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
                teleportTarget = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
                grassPatches = AccessTools.DeclaredField(typeof(ClutterSystem), "m_patches");
                grassQuality = AccessTools.DeclaredField(typeof(ClutterSystem), "m_quality");
                if (grassPatches == null || !typeof(IDictionary).IsAssignableFrom(grassPatches.FieldType) || grassQuality == null)
                    throw new InvalidOperationException("ClutterSystem patch state is missing.");
                grassForceRebuild = AccessTools.FieldRefAccess<ClutterSystem, bool>("m_forceRebuild");
                // Last, so every other prefix (the teleport unload, ValheimPlus) sees the native timer.
                Patches.Patch(update, prefix: new HarmonyMethod(typeof(FastTeleportArrival), nameof(BeforeUpdateTeleport)) { priority = Priority.Last });
                Installed = true;
                Status = "installed";
                logger.LogInfo("Fast teleport arrival installed: minimum " + minimumSeconds.Value + " s, settle " + settleSeconds.Value + " s, terrain radius " + nearRadius.Value + " m.");
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                Installed = false;
                Status = "unavailable";
                logger.LogWarning("Fast teleport arrival unavailable; the native 8 s floor stays: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void BeforeUpdateTeleport(Player __instance)
        {
            if (!Installed || !ReferenceEquals(__instance, Player.m_localPlayer)) return;
            try
            {
                if (!teleporting!(__instance)) { if (active) End(); return; }
                if (!distantTeleport!(__instance)) return;
                double now = Now;
                if (!active || !ReferenceEquals(activePlayer, __instance))
                {
                    active = true; activePlayer = __instance; Settle.Reset(); nextCheck = 0; lastBlocker = ArrivalBlocker.Moving; grassRequested = false;
                    lastCheck = now;
                }
                float timer = teleportTimer!(__instance);
                if (timer >= TeleportArrivalPolicy.NativeFloorSeconds || now < nextCheck) return;
                nextCheck = now + CheckIntervalSeconds;
                BlockerWaitMs[(int)lastBlocker] += (now - lastCheck) * 1000;
                lastCheck = now;
                var state = Observe(__instance, timer, now);
                lastBlocker = TeleportArrivalPolicy.Blocker(state, minimumSeconds!.Value, settleSeconds!.Value);
                if (lastBlocker == ArrivalBlocker.Grass && !grassRequested) RequestGrass();
                if (lastBlocker != ArrivalBlocker.None) return;
                double saved = (TeleportArrivalPolicy.NativeFloorSeconds - timer) * 1000;
                applied++;
                savedMsSum += saved;
                if (saved > savedMsMax) savedMsMax = saved;
                // Vanilla checks timer > 8 before IsAreaReady and FindFloor, both re-run this frame.
                teleportTimer(__instance) = (float)TeleportArrivalPolicy.NativeFloorSeconds + 0.01f;
                active = false;
            }
            catch (Exception exception)
            {
                failures++;
                if (failures == 1) log?.LogWarning("Fast teleport arrival check failed; this teleport keeps the native floor: " + exception.GetType().Name);
                active = false;
            }
        }

        // A teleport that reached the native floor (or ended some other way) without an early arrival.
        private static void End()
        {
            BlockerWaitMs[(int)lastBlocker] += (Now - lastCheck) * 1000;
            active = false;
            nativeFloor++;
            BlockerCounts[(int)lastBlocker]++;
        }

        private static ArrivalState Observe(Player player, float timer, double now)
        {
            var state = new ArrivalState { TeleportSeconds = timer, StableSeconds = double.NaN };
            if (timer <= AssetUnloadPolicy.TeleportMoveSeconds) return state;
            Vector3 target = teleportTarget!(player);
            ZoneSystem zones = ZoneSystem.instance;
            ZNetScene scene = ZNetScene.instance;
            ZDOMan manager = ZDOMan.instance;
            if (zones == null || scene == null || manager == null) return state;
            // IsActiveAreaLoaded walks the zones around ZNet's reference position, which still points at
            // the old area on the move frame: only trust it once that position has followed the player.
            state.ActiveAreaLoaded = ReferenceAtDestination(target) && zones.IsActiveAreaLoaded();
            state.AreaReady = scene.IsAreaReady(target);
            state.FloorFound = zones.FindFloor(target, out _);
            state.TerrainQueued = TerrainQueued(target, nearRadius!.Value);
            state.GrassReady = GrassReady(player.transform.position);
            state.DungeonPending = DungeonSpawnSlicing.PendingCount > 0;
            // The same 3x3 zone set IsAreaReady walks: a count still moving means the server is still sending.
            AreaObjects.Clear();
            manager.FindSectorObjects(ZoneSystem.GetZone(target), new SimulationDistance(1, 0), AreaObjects);
            Settle.Observe(AreaObjects.Count, now);
            state.StableSeconds = Settle.StableSeconds(now);
            return state;
        }

        internal static bool ReferenceAtDestination(Vector3 target)
        {
            ZNet net = ZNet.instance;
            return net != null && Utils.DistanceXZ(net.GetReferencePosition(), target) < 32f;
        }

        // Any rebuild still queued, for the late update (2) or Unity's LateUpdate (1).
        // Local list: a static field of this type would make the offline harness load Heightmap.
        private static bool TerrainQueued(Vector3 target, float radius)
        {
            var near = new List<Heightmap>();
            Heightmap.FindHeightmap(target, radius, near);
            foreach (Heightmap heightmap in near)
                if (heightmap.m_doLateUpdate != 0) return true;
            return false;
        }

        // Vanilla grows grass one patch per frame (~0.5 ms each, ~80 around the player). Once the terrain
        // around the destination is ready, ask for the game's own full rebuild (m_forceRebuild, what
        // ClearAll does): ~40 ms in one frame, under the loading screen instead of ~80 frames in play.
        private static void RequestGrass()
        {
            grassRequested = true;
            ClutterSystem clutter = ClutterSystem.instance;
            if (clutter == null || grassForceRebuild == null) return;
            grassForceRebuild(clutter) = true;
            grassPrebuilt++;
        }

        // Every grass patch ClutterSystem.GeneratePatches would keep around the player exists.
        private static bool GrassReady(Vector3 center)
        {
            ClutterSystem clutter = ClutterSystem.instance;
            if (clutter == null || grassQuality!.GetValue(clutter).ToString() == "Off") return true;
            if (!(grassPatches!.GetValue(clutter) is IDictionary patches)) return true;
            Vector2Int middle = clutter.GetVegPatch(center);
            int rings = Mathf.CeilToInt((clutter.m_distance - clutter.m_grassPatchSize / 2f) / clutter.m_grassPatchSize);
            for (int x = middle.x - rings; x <= middle.x + rings; x++)
            {
                for (int y = middle.y - rings; y <= middle.y + rings; y++)
                {
                    var patch = new Vector2Int(x, y);
                    if (Utils.DistanceXZ(clutter.GetVegPatchCenter(patch), center) > clutter.m_distance) continue;
                    if (!patches.Contains(patch)) return false;
                }
            }
            return true;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("fast_arrival_status", Status));
            if (!Installed) return;
            gauges.Add(new NumberValue("fast_arrival_applied", Take(ref applied), "teleports"));
            gauges.Add(new NumberValue("fast_arrival_native_floor", Take(ref nativeFloor), "teleports"));
            gauges.Add(new NumberValue("fast_arrival_saved_ms_max", Math.Round(savedMsMax, 1), "ms"));
            gauges.Add(new NumberValue("fast_arrival_saved_ms_sum", Math.Round(savedMsSum, 1), "ms"));
            gauges.Add(new NumberValue("fast_arrival_failures", Take(ref failures), "calls"));
            gauges.Add(new NumberValue("fast_arrival_grass_prebuilt", Take(ref grassPrebuilt), "teleports"));
            savedMsMax = savedMsSum = 0;
            for (int i = 1; i < BlockerWaitMs.Length; i++)
            {
                if (BlockerWaitMs[i] <= 0) continue;
                gauges.Add(new NumberValue("fast_arrival_wait_" + ((ArrivalBlocker)i).ToString().ToLowerInvariant() + "_ms", Math.Round(BlockerWaitMs[i], 1), "ms"));
                BlockerWaitMs[i] = 0;
            }
            // For teleports that fell back to the native floor: what was still loading at the last check.
            for (int i = 1; i < BlockerCounts.Length; i++)
            {
                if (BlockerCounts[i] == 0) continue;
                gauges.Add(new NumberValue("fast_arrival_held_by_" + ((ArrivalBlocker)i).ToString().ToLowerInvariant(), BlockerCounts[i], "teleports"));
                BlockerCounts[i] = 0;
            }
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset()
        {
            applied = nativeFloor = failures = grassPrebuilt = 0;
            savedMsMax = savedMsSum = 0;
            Array.Clear(BlockerCounts, 0, BlockerCounts.Length);
            Array.Clear(BlockerWaitMs, 0, BlockerWaitMs.Length);
        }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            Status = "disabled";
            active = false;
            activePlayer = null;
            teleporting = distantTeleport = null;
            teleportTimer = null;
            teleportTarget = null;
            grassPatches = grassQuality = null;
            grassForceRebuild = null;
            Reset();
        }
    }
}
