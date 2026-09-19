using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using BetterPerformance.Core;
using HarmonyLib;
using Steamworks;

namespace BetterPerformance
{
    // Read-only host facts. Nothing here changes process priority, affinity, the power
    // scheme or the system timer resolution; timeBeginPeriod is never called.
    internal static class HostTelemetry
    {
        private static bool timerUnavailable;
        private static bool counterUnavailable;

        private static bool Windows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        internal static void StartLabels(List<TextValue> labels, List<NumberValue> gauges)
        {
            labels.Add(new TextValue("host_scope", "read_only; nothing_is_changed; timer_resolution_affects_sleep_pacing"));
            gauges.Add(new NumberValue("host_logical_processors", Environment.ProcessorCount, "count"));
            if (!Windows)
            {
                labels.Add(new TextValue("host_status", "unsupported_platform"));
                return;
            }
            labels.Add(new TextValue("host_status", "available"));
            labels.Add(new TextValue("os_version", Environment.OSVersion.VersionString));
            string priority = "unavailable", affinity = "unavailable";
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    priority = process.PriorityClass.ToString();
                    ulong mask = (ulong)(long)process.ProcessorAffinity;
                    // Unity Mono reports 0 here instead of throwing; read the kernel mask directly in that case.
                    if (mask == 0 && GetProcessAffinityMask(GetCurrentProcess(), out UIntPtr processMask, out UIntPtr _))
                        mask = processMask.ToUInt64();
                    affinity = mask == 0 ? "unavailable" : "0x" + mask.ToString("x", CultureInfo.InvariantCulture);
                }
            }
            catch { }
            labels.Add(new TextValue("process_priority_class", priority));
            labels.Add(new TextValue("process_affinity_mask", affinity));
            labels.Add(new TextValue("power_scheme", PowerScheme()));
            Sample(gauges, labels);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("host_net_status", NetStats(gauges)));
            if (!Windows)
            {
                labels.Add(new TextValue("host_timer_resolution_status", "unsupported_platform"));
                labels.Add(new TextValue("host_qpc_status", "unsupported_platform"));
                return;
            }
            labels.Add(new TextValue("host_timer_resolution_status", TimerResolution(gauges)));
            labels.Add(new TextValue("host_qpc_status", Counter(gauges)));
        }

        // Steam link quality, ping and byte rates. A client reads them through
        // ZNet.GetNetStats, the call behind the game's F2 overlay. A server cannot: the
        // dedicated build of ZSteamSocket.GetConnectionQuality asks the client interface
        // SteamNetworkingSockets, which has no game-server context, so every figure
        // GetNetStats aggregates there is zero. The server path below reads each ready
        // peer's connection through SteamGameServerNetworkingSockets instead, the same
        // interface the server build's GetSendQueueSize already uses.
        private static string NetStats(List<NumberValue> gauges)
        {
            int mark = gauges.Count;
            try
            {
                var network = ZNet.instance;
                if (network == null) return "no_world";
                string? reason = Contract();
                if (reason != null) { Native(network, gauges); return "native_fallback:" + reason; }
                if (network.IsServer()) return ServerLinks(network, gauges);
                Native(network, gauges);
                PendingBytes(network, false, gauges);
                return "client_server_link";
            }
            catch (Exception exception)
            {
                if (gauges.Count > mark) gauges.RemoveRange(mark, gauges.Count - mark);
                return "unavailable:" + exception.GetType().Name;
            }
        }

        private static void Native(ZNet network, List<NumberValue> gauges)
        {
            network.GetNetStats(out float localQuality, out float remoteQuality, out int ping, out float outBytes, out float inBytes);
            gauges.Add(new NumberValue("host_net_ping_ms", ping, "ms"));
            gauges.Add(new NumberValue("host_net_out_bytes_per_sec", outBytes, "bytes_per_second"));
            gauges.Add(new NumberValue("host_net_in_bytes_per_sec", inBytes, "bytes_per_second"));
            gauges.Add(new NumberValue("host_net_quality_local", localQuality, "ratio"));
            gauges.Add(new NumberValue("host_net_quality_remote", remoteQuality, "ratio"));
        }

        // Per-peer figures over the server's ready peers. Ping is averaged as the game
        // does; the qualities are the minimum, because the worst link is the one that
        // decides what a player sees; the byte rates are sums; pending bytes is the
        // per-peer maximum, since ZDOMan.SendZDOs skips a tick for the single peer whose
        // send queue is over the cap, not for the total across peers.
        private static string ServerLinks(ZNet network, List<NumberValue> gauges)
        {
            var peers = peersRef!(network);
            bool dedicated = network.IsDedicated();
            int ready = 0, measured = 0, pingMax = 0;
            long pingTotal = 0, pendingMax = 0;
            double outTotal = 0, inTotal = 0;
            float localMin = 0f, remoteMin = 0f;
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer == null || !peer.IsReady()) continue;
                ready++;
                if (!TryRead(peer, dedicated, out SteamNetConnectionRealTimeStatus_t status)) continue;
                long pending = (long)status.m_cbPendingReliable + status.m_cbPendingUnreliable + status.m_cbSentUnackedReliable;
                if (measured == 0) { localMin = status.m_flConnectionQualityLocal; remoteMin = status.m_flConnectionQualityRemote; }
                else
                {
                    if (status.m_flConnectionQualityLocal < localMin) localMin = status.m_flConnectionQualityLocal;
                    if (status.m_flConnectionQualityRemote < remoteMin) remoteMin = status.m_flConnectionQualityRemote;
                }
                measured++;
                pingTotal += status.m_nPing;
                if (status.m_nPing > pingMax) pingMax = status.m_nPing;
                outTotal += status.m_flOutBytesPerSec;
                inTotal += status.m_flInBytesPerSec;
                if (pending > pendingMax) pendingMax = pending;
            }
            gauges.Add(new NumberValue("host_net_peers_ready", ready, "peers"));
            gauges.Add(new NumberValue("host_net_peers_measured", measured, "peers"));
            gauges.Add(new NumberValue("host_net_peers_unmeasured", ready - measured, "peers"));
            if (measured == 0) return ready == 0 ? "server_no_peers" : "server_peers_unmeasured";
            gauges.Add(new NumberValue("host_net_ping_ms", pingTotal / (double)measured, "ms"));
            gauges.Add(new NumberValue("host_net_ping_max_ms", pingMax, "ms"));
            gauges.Add(new NumberValue("host_net_quality_local", localMin, "ratio"));
            gauges.Add(new NumberValue("host_net_quality_remote", remoteMin, "ratio"));
            gauges.Add(new NumberValue("host_net_out_bytes_per_sec", outTotal, "bytes_per_second"));
            gauges.Add(new NumberValue("host_net_in_bytes_per_sec", inTotal, "bytes_per_second"));
            gauges.Add(new NumberValue("host_net_pending_bytes_max", pendingMax, "bytes"));
            return "server_per_peer";
        }

        // A client's own upload congestion against the same cap. Its peer list holds the
        // server link only, so the maximum is that one connection.
        private static void PendingBytes(ZNet network, bool dedicated, List<NumberValue> gauges)
        {
            var peers = peersRef!(network);
            long pendingMax = -1;
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer == null || !peer.IsReady()) continue;
                if (!TryRead(peer, dedicated, out SteamNetConnectionRealTimeStatus_t status)) continue;
                long pending = (long)status.m_cbPendingReliable + status.m_cbPendingUnreliable + status.m_cbSentUnackedReliable;
                if (pending > pendingMax) pendingMax = pending;
            }
            if (pendingMax >= 0) gauges.Add(new NumberValue("host_net_pending_bytes_max", pendingMax, "bytes"));
        }

        // A dedicated server holds a game-server Steam context; a listen-server host and a
        // client hold the ordinary client one. Asking the wrong interface returns a
        // non-OK EResult, which counts the peer as unmeasured rather than as zero.
        private static bool TryRead(ZNetPeer peer, bool dedicated, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = default(SteamNetConnectionRealTimeStatus_t);
            var socket = SteamTelemetry.SteamSocket(peer.m_socket);
            if (socket == null) return false;
            HSteamNetConnection connection = connectionRef!(socket);
            var lane = default(SteamNetConnectionRealTimeLaneStatus_t);
            EResult response = dedicated
                ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane)
                : SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane);
            return response == EResult.k_EResultOK;
        }

        private static bool contractChecked;
        private static string? contractReason;
        private static AccessTools.FieldRef<ZNet, List<ZNetPeer>>? peersRef;
        private static AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection>? connectionRef;

        // Checked once. A missing field or a changed Steam signature falls back to the
        // native call rather than throwing every interval.
        private static string? Contract()
        {
            if (contractChecked) return contractReason;
            contractChecked = true;
            contractReason = Resolve();
            return contractReason;
        }

        private static string? Resolve()
        {
            try
            {
                var peers = AccessTools.Field(typeof(ZNet), "m_peers");
                if (peers == null || peers.IsStatic || peers.FieldType != typeof(List<ZNetPeer>)) return "znet_m_peers";
                var connection = AccessTools.Field(typeof(ZSteamSocket), "m_con");
                if (connection == null || connection.IsStatic || connection.FieldType != typeof(HSteamNetConnection)) return "zsteamsocket_m_con";
                var socket = AccessTools.Field(typeof(ZNetPeer), "m_socket");
                if (socket == null || socket.IsStatic || socket.FieldType != typeof(ISocket)) return "znetpeer_m_socket";
                if (!HasRealTimeStatus(typeof(SteamNetworkingSockets))) return "steam_client_api";
                if (!HasRealTimeStatus(typeof(SteamGameServerNetworkingSockets))) return "steam_gameserver_api";
                peersRef = AccessTools.FieldRefAccess<ZNet, List<ZNetPeer>>("m_peers");
                connectionRef = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
                return null;
            }
            catch (Exception exception) { return exception.GetType().Name; }
        }

        internal static bool HasRealTimeStatus(Type declaring)
        {
            var method = declaring.GetMethod("GetConnectionRealTimeStatus",
                BindingFlags.Public | BindingFlags.Static, null,
                new[]
                {
                    typeof(HSteamNetConnection),
                    typeof(SteamNetConnectionRealTimeStatus_t).MakeByRefType(),
                    typeof(int),
                    typeof(SteamNetConnectionRealTimeLaneStatus_t).MakeByRefType()
                }, null);
            return method != null && method.ReturnType == typeof(EResult);
        }

        // Resolution is reported in 100ns units. The "minimum" value is the coarsest
        // period the kernel supports and the "maximum" the finest, matching ntdll.
        private static string TimerResolution(List<NumberValue> gauges)
        {
            if (timerUnavailable) return "native_api_unavailable";
            try
            {
                uint coarsest, finest, current;
                if (NtQueryTimerResolution(out coarsest, out finest, out current) != 0) return "read_failed";
                gauges.Add(new NumberValue("host_timer_resolution_current_ms", current / 10000.0, "ms"));
                gauges.Add(new NumberValue("host_timer_resolution_min_ms", finest / 10000.0, "ms"));
                gauges.Add(new NumberValue("host_timer_resolution_max_ms", coarsest / 10000.0, "ms"));
                return "available";
            }
            catch { timerUnavailable = true; return "native_api_unavailable"; }
        }

        // The raw kernel counter is the only clock shared by the client and the dedicated
        // server on this host; Stopwatch origins differ between the two Unity processes.
        private static string Counter(List<NumberValue> gauges)
        {
            if (counterUnavailable) return "native_api_unavailable";
            try
            {
                long timestamp, frequency;
                if (!QueryPerformanceCounter(out timestamp) || !QueryPerformanceFrequency(out frequency) || frequency <= 0)
                    return "read_failed";
                gauges.Add(new NumberValue("host_qpc_timestamp", timestamp, "counts"));
                gauges.Add(new NumberValue("host_qpc_frequency", frequency, "counts_per_second"));
                return "available";
            }
            catch { counterUnavailable = true; return "native_api_unavailable"; }
        }

        private static string PowerScheme()
        {
            IntPtr scheme = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0 || scheme == IntPtr.Zero) return "unavailable";
                uint size = 0;
                if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 ||
                    size == 0 || size > 4096) return "unavailable";
                buffer = Marshal.AllocHGlobal((int)size);
                if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return "unavailable";
                return Marshal.PtrToStringUni(buffer) ?? "unavailable";
            }
            catch { return "unavailable"; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                // PowerGetActiveScheme allocates the GUID with LocalAlloc.
                if (scheme != IntPtr.Zero) LocalFree(scheme);
            }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceCounter(out long count);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceFrequency(out long frequency);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);
    }
}
