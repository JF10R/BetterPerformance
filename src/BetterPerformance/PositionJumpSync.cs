using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // A client sends its reference position to the server every 2 s (ZNet.SendPeriodicData) and the
    // server relays the player list on its own 2 s timer, so after a portal the server keeps streaming
    // the old area and the map shows the old position for 0-4 s. The observers always measure those
    // delays; the two opt-in switches send the jump at once (client) and relay the list at once
    // (server). Same RPCs and values as vanilla. docs/position-jump-sync.md.
    internal static class PositionJumpSync
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".PositionJumpSync");
        private static ConfigEntry<bool>? sendOption, relayOption;
        private static ConfigEntry<float>? jumpMeters;
        private static Action<ZNet, ZNetPeer>? sendPosition;
        private static Action<ZNet>? sendPlayerList;
        private static Func<ZNet, ZRpc, ZNetPeer>? peerOf;
        private static bool hasSent, earlySending, relayPending;
        private static Vector3 lastSent;
        private static double clientJumpAt = double.NaN, serverJumpAt = double.NaN, lastEarly = double.NaN, lastRelay = double.NaN;
        // Client: jumps seen, early sends, all sends (cadence check), the delay from jump to send.
        private static long jumps, earlySends, sends, failures;
        private static double jumpMetersMax, sendDelayMsMax, sendDelayMsSum;
        private static long sendDelays;
        // Server: jumps received, early relays, all list sends, the delay from receipt to list.
        private static long received, relays, listSends;
        private static double listDelayMsMax, listDelayMsSum;
        private static long listDelays;

        internal static bool Installed { get; private set; }
        internal static bool SendEnabled => Installed && sendOption != null && sendOption.Value;
        internal static bool RelayEnabled => Installed && relayOption != null && relayOption.Value;
        internal static string Status { get; private set; } = "disabled";
        // Client: when (Stopwatch seconds) and where the last reference position went to the server,
        // native or early; FastTeleportArrival waits for the destination to have been sent. NaN: none yet.
        internal static double LastSentAt { get; private set; } = double.NaN;
        internal static Vector3 LastSentPosition => lastSent;
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        private static double Threshold => jumpMeters == null ? PositionJumpPolicy.DefaultJumpMeters : jumpMeters.Value;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            sendOption = config.Bind("Teleport", "SendPositionOnJumpEnabled", false,
                "Client: after a portal or respawn moves the player, send the position to the server at once instead of within 2 s, so the server " +
                "streams the destination sooner and other players' maps update. Same message and value as the native one. Requires restart.");
            relayOption = config.Bind("Teleport", "RelayPlayerListOnJumpEnabled", false,
                "Dedicated server: when a player's received position jumps, send the player list (map positions) to everyone at once instead of " +
                "within 2 s. Same message as the native one. Requires restart.");
            jumpMeters = config.Bind("Teleport", "PositionJumpMeters", (float)PositionJumpPolicy.DefaultJumpMeters,
                new ConfigDescription("Distance from the last sent position that counts as a jump.", new AcceptableValueRange<float>(16f, 1000f)));
            try
            {
                var send = AccessTools.DeclaredMethod(typeof(ZNet), "SendServerSyncPlayerData", new[] { typeof(ZNetPeer) });
                var list = AccessTools.DeclaredMethod(typeof(ZNet), "SendPlayerList", Type.EmptyTypes);
                var receive = AccessTools.DeclaredMethod(typeof(ZNet), "RPC_ServerSyncedPlayerData", new[] { typeof(ZRpc), typeof(ZPackage) });
                var getPeer = AccessTools.DeclaredMethod(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) });
                if (send == null || send.ReturnType != typeof(void) || list == null || list.ReturnType != typeof(void) ||
                    receive == null || receive.ReturnType != typeof(void) || getPeer == null || getPeer.ReturnType != typeof(ZNetPeer))
                    throw new InvalidOperationException("ZNet position sync methods are missing.");
                sendPosition = AccessTools.MethodDelegate<Action<ZNet, ZNetPeer>>(send);
                sendPlayerList = AccessTools.MethodDelegate<Action<ZNet>>(list);
                peerOf = AccessTools.MethodDelegate<Func<ZNet, ZRpc, ZNetPeer>>(getPeer);
                Patches.Patch(send, prefix: new HarmonyMethod(typeof(PositionJumpSync), nameof(BeforeSendPosition)));
                Patches.Patch(list, prefix: new HarmonyMethod(typeof(PositionJumpSync), nameof(BeforeSendPlayerList)));
                Patches.Patch(receive, prefix: new HarmonyMethod(typeof(PositionJumpSync), nameof(BeforeReceive)),
                    postfix: new HarmonyMethod(typeof(PositionJumpSync), nameof(AfterReceive)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Position jump sync installed: early send " + sendOption.Value + ", early player list " + relayOption.Value
                    + ", jump " + Threshold + " m.");
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                Installed = false;
                Status = "unavailable";
                sendPosition = null; sendPlayerList = null; peerOf = null;
                logger.LogWarning("Position jump sync unavailable; native 2 s cadence kept: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        // Every native (or early) send of the client's reference position to one peer.
        private static void BeforeSendPosition(ZNet __instance)
        {
            try
            {
                if (__instance.IsServer()) return;
                double now = Now;
                sends++;
                lastSent = __instance.GetReferencePosition();
                LastSentAt = now;
                hasSent = true;
                if (!double.IsNaN(clientJumpAt))
                {
                    double ms = Math.Max(0, now - clientJumpAt) * 1000;
                    sendDelays++;
                    sendDelayMsSum += ms;
                    if (ms > sendDelayMsMax) sendDelayMsMax = ms;
                    clientJumpAt = double.NaN;
                }
                if (earlySending) earlySends++;
            }
            catch { failures++; }
        }

        private static void BeforeSendPlayerList(ZNet __instance)
        {
            try
            {
                if (!__instance.IsServer()) return;
                listSends++;
                if (double.IsNaN(serverJumpAt)) return;
                double ms = Math.Max(0, Now - serverJumpAt) * 1000;
                listDelays++;
                listDelayMsSum += ms;
                if (ms > listDelayMsMax) listDelayMsMax = ms;
                serverJumpAt = double.NaN;
            }
            catch { failures++; }
        }

        internal struct ReceiveState { internal ZNetPeer? Peer; internal Vector3 Before; }

        private static void BeforeReceive(ZNet __instance, ZRpc rpc, out ReceiveState __state)
        {
            __state = default;
            try
            {
                if (!__instance.IsServer() || peerOf == null) return;
                ZNetPeer peer = peerOf(__instance, rpc);
                if (peer != null) __state = new ReceiveState { Peer = peer, Before = peer.m_refPos };
            }
            catch { failures++; }
        }

        private static void AfterReceive(ReceiveState __state)
        {
            if (__state.Peer == null) return;
            try
            {
                // The first report after a join moves from the origin; that is not a jump.
                if (__state.Before == Vector3.zero) return;
                if (!PositionJumpPolicy.IsJump(Vector3.Distance(__state.Before, __state.Peer.m_refPos), Threshold)) return;
                received++;
                if (double.IsNaN(serverJumpAt)) serverJumpAt = Now;
                if (RelayEnabled) relayPending = true;
            }
            catch { failures++; }
        }

        // Main thread, every frame (Plugin.Update): one distance check, a send only after a jump.
        internal static void Pump()
        {
            if (!Installed) return;
            ZNet net = ZNet.instance;
            if (net == null) { hasSent = false; relayPending = false; clientJumpAt = serverJumpAt = LastSentAt = double.NaN; return; }
            try
            {
                double now = Now;
                if (net.IsServer())
                {
                    if (!relayPending || !PositionJumpPolicy.MaySend(now, lastRelay) || sendPlayerList == null) return;
                    relayPending = false;
                    lastRelay = now;
                    relays++;
                    sendPlayerList(net);
                    return;
                }
                // A detected jump waits for its send (early, or the native one that clears it).
                if (!double.IsNaN(clientJumpAt)) { TrySend(net, now); return; }
                if (!hasSent) return;
                float distance = Vector3.Distance(net.GetReferencePosition(), lastSent);
                if (!PositionJumpPolicy.IsJump(distance, Threshold)) return;
                jumps++;
                if (distance > jumpMetersMax) jumpMetersMax = distance;
                clientJumpAt = now;
                TrySend(net, now);
            }
            catch { failures++; }
        }

        private static void TrySend(ZNet net, double now)
        {
            if (!SendEnabled || sendPosition == null || !PositionJumpPolicy.MaySend(now, lastEarly)) return;
            ZNetPeer peer = net.GetServerPeer();
            if (peer == null || !peer.IsReady()) return;
            lastEarly = now;
            earlySending = true;
            try { sendPosition(net, peer); }
            finally { earlySending = false; }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("position_jump_sync_status", Status));
            if (!Installed) return;
            labels.Add(new TextValue("position_jump_send_enabled", SendEnabled ? "true" : "false"));
            labels.Add(new TextValue("position_jump_relay_enabled", RelayEnabled ? "true" : "false"));
            gauges.Add(new NumberValue("position_sync_sends", Take(ref sends), "sends"));
            gauges.Add(new NumberValue("position_jump_detected", Take(ref jumps), "jumps"));
            gauges.Add(new NumberValue("position_jump_early_sends", Take(ref earlySends), "sends"));
            gauges.Add(new NumberValue("position_jump_distance_max_m", Math.Round(jumpMetersMax, 1), "m"));
            gauges.Add(new NumberValue("position_jump_send_delays", Take(ref sendDelays), "jumps"));
            gauges.Add(new NumberValue("position_jump_send_delay_ms_max", Math.Round(sendDelayMsMax, 1), "ms"));
            gauges.Add(new NumberValue("position_jump_send_delay_ms_sum", Math.Round(sendDelayMsSum, 1), "ms"));
            gauges.Add(new NumberValue("position_jump_received", Take(ref received), "jumps"));
            gauges.Add(new NumberValue("player_list_sends", Take(ref listSends), "sends"));
            gauges.Add(new NumberValue("player_list_early_sends", Take(ref relays), "sends"));
            gauges.Add(new NumberValue("player_list_jump_delays", Take(ref listDelays), "jumps"));
            gauges.Add(new NumberValue("player_list_jump_delay_ms_max", Math.Round(listDelayMsMax, 1), "ms"));
            gauges.Add(new NumberValue("player_list_jump_delay_ms_sum", Math.Round(listDelayMsSum, 1), "ms"));
            gauges.Add(new NumberValue("position_jump_failures", Take(ref failures), "calls"));
            jumpMetersMax = sendDelayMsMax = sendDelayMsSum = listDelayMsMax = listDelayMsSum = 0;
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }

        internal static void Reset()
        {
            jumps = earlySends = sends = failures = sendDelays = received = relays = listSends = listDelays = 0;
            jumpMetersMax = sendDelayMsMax = sendDelayMsSum = listDelayMsMax = listDelayMsSum = 0;
        }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            Status = "disabled";
            sendPosition = null; sendPlayerList = null; peerOf = null;
            hasSent = relayPending = false;
            clientJumpAt = serverJumpAt = lastEarly = lastRelay = LastSentAt = double.NaN;
            Reset();
        }
    }
}
