using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Minimap.UpdatePlayerPins glides another player's pin toward its new position at 200 m/s
    // (Vector3.MoveTowards), so after a 1.6 km portal the pin needs 8 s to arrive, 20 s for 4 km.
    // This places the pin at once when the received position jumped farther than the jump threshold;
    // ordinary walking keeps the native glide. Client only, display only. docs/position-jump-sync.md.
    internal static class MinimapPlayerPinSnap
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".MinimapPlayerPinSnap");
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<float>? jumpMeters;
        private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>>? playerPins;
        private static AccessTools.FieldRef<Minimap, List<ZNet.PlayerInfo>>? playerInfo;
        private static AccessTools.FieldRef<Minimap, bool>? pinUpdateRequired;
        private static long snaps, failures;
        private static double snapMetersMax;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Map", "SnapPlayerPinsOnJumpEnabled", false,
                "Client: when another player's map position jumps (portal, respawn), place their pin there at once instead of gliding it " +
                "at 200 m/s (8 s for a 1.6 km portal). Display only. Requires restart.");
            jumpMeters = config.Bind("Map", "PlayerPinJumpMeters", (float)PositionJumpPolicy.DefaultJumpMeters,
                new ConfigDescription("Distance between a pin and its received position that counts as a jump.", new AcceptableValueRange<float>(16f, 1000f)));
            if (!option.Value) { Status = "disabled"; return; }
            try
            {
                var update = AccessTools.DeclaredMethod(typeof(Minimap), "UpdatePlayerPins", new[] { typeof(float) })
                    ?? throw new InvalidOperationException("Minimap.UpdatePlayerPins(float) is missing.");
                if (update.IsStatic || update.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported Minimap.UpdatePlayerPins signature.");
                playerPins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_playerPins");
                playerInfo = AccessTools.FieldRefAccess<Minimap, List<ZNet.PlayerInfo>>("m_tempPlayerInfo");
                pinUpdateRequired = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");
                Patches.Patch(update, postfix: new HarmonyMethod(typeof(MinimapPlayerPinSnap), nameof(AfterUpdatePlayerPins)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Minimap player pin snap installed: jump " + jumpMeters.Value + " m.");
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                Installed = false;
                Status = "unavailable";
                playerPins = null; playerInfo = null; pinUpdateRequired = null;
                logger.LogWarning("Minimap player pin snap unavailable; native glide kept: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        // Vanilla pairs pins and players by index and moves a pin only when the names match; same here.
        private static void AfterUpdatePlayerPins(Minimap __instance)
        {
            try
            {
                var pins = playerPins!(__instance);
                var players = playerInfo!(__instance);
                float threshold = jumpMeters!.Value;
                int count = Math.Min(pins.Count, players.Count);
                for (int i = 0; i < count; i++)
                {
                    Minimap.PinData pin = pins[i];
                    ZNet.PlayerInfo player = players[i];
                    if (pin == null || pin.m_name != player.m_name) continue;
                    float distance = Vector3.Distance(pin.m_pos, player.m_position);
                    if (!PositionJumpPolicy.IsJump(distance, threshold)) continue;
                    pin.m_pos = player.m_position;
                    pinUpdateRequired!(__instance) = true;
                    snaps++;
                    if (distance > snapMetersMax) snapMetersMax = distance;
                }
            }
            catch { failures++; }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("map_pin_snap_status", Status));
            if (!Installed) return;
            gauges.Add(new NumberValue("map_pin_snaps", Take(ref snaps), "pins"));
            gauges.Add(new NumberValue("map_pin_snap_distance_max_m", Math.Round(snapMetersMax, 1), "m"));
            gauges.Add(new NumberValue("map_pin_snap_failures", Take(ref failures), "calls"));
            snapMetersMax = 0;
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset()
        {
            snaps = failures = 0;
            snapMetersMax = 0;
        }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            Status = "disabled";
            playerPins = null; playerInfo = null; pinUpdateRequired = null;
            Reset();
        }
    }
}
