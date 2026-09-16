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
        private static readonly FieldInfo? OnlineBackend = AccessTools.Field(typeof(ZNet), "m_onlineBackend");
        private static readonly Dictionary<Type, FieldInfo?> WrapperFields = new Dictionary<Type, FieldInfo?>();

        // Verified against com.rlabrecque.steamworks.net.dll shipped with Valheim 1.0.12:
        // Constants.k_nSteamNetworkConnectionInfoFlags_Relayed == 16 (8 is _Fast).
        private const int RelayedFlag = 16;

        private static ISocket? Unwrap(ISocket? socket)
        {
            // ServerSync can nest a read-through buffering wrapper for each mod's config sync.
            // Inspect only this known wrapper; never invoke transport methods or alter it.
            for (int depth = 0; socket != null && depth < 8; depth++)
            {
                Type type = socket.GetType();
                if (type.FullName != "ServerSync.ConfigSync+SendConfigsAfterLogin+BufferingSocket") return socket;
                if (!WrapperFields.TryGetValue(type, out var field))
                {
                    field = AccessTools.Field(type, "Original");
                    WrapperFields[type] = field;
                }
                socket = field?.GetValue(socket) as ISocket;
            }
            return socket;
        }

        private static ZSteamSocket? SteamSocket(ISocket? socket) => Unwrap(socket) as ZSteamSocket;

        // SteamNetworkingPOPID packs 3-4 ASCII characters identifying a Valve datacenter
        // (for example "ord", "fra"). It is a location code, never a peer identity.
        private static string DecodePop(uint id)
        {
            if (id == 0) return "none";
            var decoded = new char[4];
            int length = 0;
            for (int shift = 24; shift >= 0; shift -= 8)
            {
                int value = (int)((id >> shift) & 0xFF);
                if (value == 0)
                {
                    if (length == 0) continue;
                    return "invalid";
                }
                char character = char.ToLowerInvariant((char)value);
                if ((character < 'a' || character > 'z') && (character < '0' || character > '9')) return "invalid";
                decoded[length++] = character;
            }
            return length == 0 ? "none" : new string(decoded, 0, length);
        }

        private static string Backend()
        {
            try
            {
                object? value = OnlineBackend?.GetValue(null);
                return value == null ? "unavailable" : value.ToString();
            }
            catch { return "unavailable"; }
        }

        internal static void Sample(List<ZNetPeer> peers, List<NumberValue> gauges, List<TextValue> labels)
        {
            int sampled = 0, unsupported = 0, unavailable = 0;
            long pendingReliable = 0, pendingUnreliable = 0, unacked = 0;
            double incoming = 0, outgoing = 0;
            int maxPing = -1;
            long maxQueueMicros = -1;
            int relayed = 0, direct = 0, infoUnavailable = 0;
            int steamSockets = 0, playFabSockets = 0, otherSockets = 0;
            string relayPop = "none";
            bool dedicated = ZNet.instance.IsDedicated();
            foreach (var peer in peers)
            {
                try
                {
                    var unwrapped = Unwrap(peer.m_socket);
                    if (unwrapped is ZSteamSocket) steamSockets++;
                    else if (unwrapped != null && unwrapped.GetType().Name == "ZPlayFabSocket") playFabSockets++;
                    else otherSockets++;

                    var socket = unwrapped as ZSteamSocket;
                    if (socket == null) { unsupported++; continue; }
                    if (Connection == null || !(Connection.GetValue(socket) is HSteamNetConnection connection))
                    { unavailable++; continue; }
                    var lane = new SteamNetConnectionRealTimeLaneStatus_t();
                    var status = new SteamNetConnectionRealTimeStatus_t();
                    // Dedicated and client assemblies call different Steam interfaces.
                    var response = dedicated
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

                    // Path classification only. Addresses and identities are never read or exported.
                    SteamNetConnectionInfo_t info;
                    bool described = dedicated
                        ? SteamGameServerNetworkingSockets.GetConnectionInfo(connection, out info)
                        : SteamNetworkingSockets.GetConnectionInfo(connection, out info);
                    if (!described) { infoUnavailable++; continue; }
                    uint relayId = (uint)info.m_idPOPRelay;
                    if ((info.m_nFlags & RelayedFlag) != 0 || relayId != 0)
                    {
                        relayed++;
                        string code = DecodePop(relayId);
                        if (code != "none")
                        {
                            if (relayPop == "none") relayPop = code;
                            else if (relayPop != code) relayPop = "multiple";
                        }
                    }
                    else direct++;
                }
                catch { unavailable++; }
            }
            gauges.Add(new NumberValue("steam_connections_sampled", sampled, "connections"));
            gauges.Add(new NumberValue("steam_connections_unavailable", unavailable, "connections"));
            gauges.Add(new NumberValue("steam_connections_unsupported", unsupported, "connections"));
            gauges.Add(new NumberValue("steam_connections_relayed", relayed, "connections"));
            gauges.Add(new NumberValue("steam_connections_direct", direct, "connections"));
            gauges.Add(new NumberValue("steam_connections_info_unavailable", infoUnavailable, "connections"));
            gauges.Add(new NumberValue("peer_sockets_steam", steamSockets, "sockets"));
            gauges.Add(new NumberValue("peer_sockets_playfab", playFabSockets, "sockets"));
            gauges.Add(new NumberValue("peer_sockets_other", otherSockets, "sockets"));
            labels.Add(new TextValue("steam_transport_path",
                relayed > 0 && direct > 0 ? "mixed" : relayed > 0 ? "relayed" : direct > 0 ? "direct" : "unknown"));
            labels.Add(new TextValue("steam_relay_pop", relayPop));
            labels.Add(new TextValue("online_backend", Backend()));
            labels.Add(new TextValue("steam_queue_scope", "native transport only; excludes game/mod managed queues"));
            labels.Add(new TextValue("steam_transport_scope", "flags_from_GetConnectionInfo; relayed_LAN_link_adds_latency; no_addresses_exported"));
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
