using System;
using System.Collections.Generic;
using System.Reflection;
using BetterPerformance.Core;
using HarmonyLib;
using Steamworks;

namespace BetterPerformance
{
    internal static class SteamTelemetry
    {
        private static readonly FieldInfo? Connection = AccessTools.Field(typeof(ZSteamSocket), "m_con");
        private static readonly Dictionary<Type, FieldInfo?> WrapperFields = new Dictionary<Type, FieldInfo?>();

        private static ZSteamSocket? SteamSocket(ISocket? socket)
        {
            // ServerSync can nest a read-through buffering wrapper for each mod's config sync.
            // Inspect only this known wrapper; never invoke transport methods or alter it.
            for (int depth = 0; socket != null && depth < 8; depth++)
            {
                if (socket is ZSteamSocket steam) return steam;
                Type type = socket.GetType();
                if (type.FullName != "ServerSync.ConfigSync+SendConfigsAfterLogin+BufferingSocket") return null;
                if (!WrapperFields.TryGetValue(type, out var field))
                {
                    field = AccessTools.Field(type, "Original");
                    WrapperFields[type] = field;
                }
                socket = field?.GetValue(socket) as ISocket;
            }
            return null;
        }

        internal static void Sample(List<ZNetPeer> peers, List<NumberValue> gauges, List<TextValue> labels)
        {
            int sampled = 0, unsupported = 0, unavailable = 0;
            long pendingReliable = 0, pendingUnreliable = 0, unacked = 0;
            double incoming = 0, outgoing = 0;
            int maxPing = -1;
            long maxQueueMicros = -1;
            foreach (var peer in peers)
            {
                try
                {
                    var socket = SteamSocket(peer.m_socket);
                    if (socket == null) { unsupported++; continue; }
                    if (Connection == null || !(Connection.GetValue(socket) is HSteamNetConnection connection))
                    { unavailable++; continue; }
                    var lane = new SteamNetConnectionRealTimeLaneStatus_t();
                    var status = new SteamNetConnectionRealTimeStatus_t();
                    // Dedicated and client assemblies call different Steam interfaces.
                    var response = ZNet.instance.IsDedicated()
                        ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane)
                        : SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane);
                    if (response != EResult.k_EResultOK)
                    { unavailable++; continue; }
                    sampled++;
                    pendingReliable += status.m_cbPendingReliable;
                    pendingUnreliable += status.m_cbPendingUnreliable;
                    unacked += status.m_cbSentUnackedReliable;
                    incoming += status.m_flInBytesPerSec;
                    outgoing += status.m_flOutBytesPerSec;
                    maxPing = Math.Max(maxPing, status.m_nPing);
                    maxQueueMicros = Math.Max(maxQueueMicros, (long)status.m_usecQueueTime);
                }
                catch { unavailable++; }
            }
            gauges.Add(new NumberValue("steam_connections_sampled", sampled, "connections"));
            gauges.Add(new NumberValue("steam_connections_unavailable", unavailable, "connections"));
            gauges.Add(new NumberValue("steam_connections_unsupported", unsupported, "connections"));
            labels.Add(new TextValue("steam_queue_scope", "native transport only; excludes game/mod managed queues"));
            if (sampled == 0) return;
            gauges.Add(new NumberValue("steam_pending_reliable", pendingReliable, "bytes"));
            gauges.Add(new NumberValue("steam_pending_unreliable", pendingUnreliable, "bytes"));
            gauges.Add(new NumberValue("steam_sent_unacked_reliable", unacked, "bytes"));
            gauges.Add(new NumberValue("steam_in_rate", incoming, "bytes_per_second"));
            gauges.Add(new NumberValue("steam_out_rate", outgoing, "bytes_per_second"));
            if (maxPing >= 0) gauges.Add(new NumberValue("steam_ping_max", maxPing, "ms"));
            if (maxQueueMicros >= 0) gauges.Add(new NumberValue("steam_queue_time_max", maxQueueMicros / 1000.0, "ms"));
        }
    }
}
