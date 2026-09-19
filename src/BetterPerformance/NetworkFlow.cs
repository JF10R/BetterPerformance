using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using Steamworks;

namespace BetterPerformance
{
    // Replaces two fixed numbers with measured ones. ZDOMan.SendZDOs gates a peer's ZDO
    // batch on a constant 10,240 B send queue, and ZSteamSocket pins Steam's send rate to
    // 153,600 B/s for every link; BetterNetworking replaced both with menu constants. A
    // constant cannot fit a LAN link and a relayed WAN link at once: on a 1 ms LAN link
    // 10 KB is one tick of headroom, so a single loss burst skips the send tick, which is
    // what backed a 27 KB queue up behind 1-4 s of loot on 2026-09-18.
    // Here the window is the peer's own bandwidth-delay product with a margin, recomputed
    // from Steam's rate and ping estimates, and the rate ceiling follows the link class.
    //
    // Cost: one SteamNetConnectionRealTimeStatus_t read per peer per 250 ms. The two
    // transpiled sites cost one field read, one dictionary lookup and one compare per
    // SendZDOs call. Nothing allocates after a peer's first window.
    internal static class NetworkFlow
    {
        private const int VanillaWindow = 10240;
        private const int FailureLimit = 8;
        private const double SampleIntervalSeconds = 0.25;
        private const double PolicyReviewSeconds = 30.0;
        // A relayed link is never LAN however low its ping reads.
        private const int LanPingMs = 5;
        private const int LanRate = 1048576;
        private const int WanRateMin = 153600;   // vanilla's global pin, kept as the WAN floor
        private const int WanRateMax = 1048576;
        private const int SendBufferBytes = 524288;
        // Verified against com.rlabrecque.steamworks.net.dll shipped with the game:
        // Constants.k_nSteamNetworkConnectionInfoFlags_Relayed == 16 (8 is _Fast).
        private const int RelayedFlag = 16;

        private enum LinkClass { Unknown, Lan, Wan }

        private sealed class Link
        {
            internal double LastSampleSeconds = double.NegativeInfinity;
            internal double LastPolicySeconds = double.NegativeInfinity;
            internal LinkClass Class = LinkClass.Unknown;
        }

        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".NetworkFlow");
        private static readonly string OwnOwnerPrefix = Plugin.PluginId + ".";
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly object Gate = new object();
        private static readonly SendWindowController Controller = new SendWindowController();
        private static readonly Dictionary<long, Link> Links = new Dictionary<long, Link>();
        private static readonly List<long> Expired = new List<long>();

        private static ConfigEntry<bool>? option;
        private static AccessTools.FieldRef<object, ZNetPeer>? peerRef;
        private static AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection>? connectionRef;
        private static AccessTools.FieldRef<ZNet, List<ZNetPeer>>? netPeersRef;
        private static bool failed, bufferApplied, dedicatedKnown, dedicated;
        private static long rateSets, failures, failureTotal;

        internal static bool Installed { get; private set; }
        internal static bool Enabled => Installed && !failed && option != null && option.Value;
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            try
            {
                option = config.Bind("Network", "AdaptiveFlowEnabled", false,
                    "Adaptive per-peer send window replacing the fixed 10 KB ZDO send gate, plus a Steam send-rate policy read from the measured link. Replaces BetterNetworking's Queue Size and Send Rate settings. Requires a restart.");
                // Off means no patch at all: the rewritten site is on the send path and a
                // disabled module would still cost a call per SendZDOs for nothing.
                if (!option.Value) { Installed = false; Status = "disabled"; return; }
                if (BetterNetworkingLoaded())
                {
                    Installed = false;
                    Status = "yield_to_betternetworking";
                    logger.LogInfo("Adaptive network flow yields to BetterNetworking; the fixed send queue and send rate stay under that mod's control.");
                    return;
                }
                Verify();
                string? foreign = ForeignOwner();
                if (foreign != null)
                {
                    Installed = false;
                    Status = "foreign_patch:" + foreign;
                    logger.LogWarning("Adaptive network flow not installed: " + foreign + " already patches the send gate; vanilla behaviour retained.");
                    return;
                }
                Patches.Patch(Contract.SendZDOs!, transpiler: new HarmonyMethod(typeof(NetworkFlow), nameof(Transpile)));
                Patches.Patch(Contract.Disconnect!, postfix: new HarmonyMethod(typeof(NetworkFlow), nameof(AfterDisconnect)));
                Installed = true;
                failed = false;
                Status = "installed";
                logger.LogInfo("Adaptive network flow installed; the 10 KB send gate is now a per-peer window and the send rate follows the link class.");
            }
            catch (Exception exception)
            {
                Installed = false;
                Status = "unavailable_unexpected_shape";
                // A failed rollback must not escape: Installed=false already makes the hook
                // return the vanilla constant, and an exception here would abort start-up.
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                logger.LogWarning("Adaptive network flow unavailable; the native send gate is retained: " + exception.Message);
            }
        }

        // Chainloader's static initializer needs the BepInEx runtime; outside the game (the
        // contract harness) it throws, which must read as "not loaded", not as a broken install.
        private static bool BetterNetworkingLoaded()
        {
            try { return Chainloader.PluginInfos.ContainsKey("CW_Jesse.BetterNetworking"); }
            catch { return false; }
        }

        private static class Contract
        {
            internal static MethodInfo? SendZDOs, QueueSize, Disconnect;
            internal static Type? Peer;
        }

        // Every member the transpiled site and the hook touch must exist with the exact
        // expected signature, and the body must carry the shape the rewrite assumes.
        // Throws when anything differs, so Install falls back to native behaviour.
        internal static void Verify()
        {
            Contract.Peer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
            if (Contract.Peer == null)
                throw new InvalidOperationException("ZDOMan.ZDOPeer no longer exists.");
            Contract.SendZDOs = AccessTools.DeclaredMethod(typeof(ZDOMan), "SendZDOs", new[] { Contract.Peer, typeof(bool) });
            if (Contract.SendZDOs == null || Contract.SendZDOs.IsStatic || Contract.SendZDOs.ReturnType != typeof(bool))
                throw new InvalidOperationException("ZDOMan.SendZDOs(ZDOPeer, bool) is not an instance bool method.");
            Contract.QueueSize = AccessTools.DeclaredMethod(typeof(ISocket), "GetSendQueueSize", Type.EmptyTypes);
            if (Contract.QueueSize == null || Contract.QueueSize.ReturnType != typeof(int))
                throw new InvalidOperationException("ISocket.GetSendQueueSize() no longer returns an int.");
            var peerField = AccessTools.DeclaredField(Contract.Peer, "m_peer");
            if (peerField == null || peerField.IsStatic || peerField.FieldType != typeof(ZNetPeer))
                throw new InvalidOperationException("ZDOPeer.m_peer is not a ZNetPeer field.");
            var uid = AccessTools.DeclaredField(typeof(ZNetPeer), "m_uid");
            if (uid == null || uid.IsStatic || uid.FieldType != typeof(long))
                throw new InvalidOperationException("ZNetPeer.m_uid is not a long field.");
            var socket = AccessTools.DeclaredField(typeof(ZNetPeer), "m_socket");
            if (socket == null || socket.IsStatic || socket.FieldType != typeof(ISocket))
                throw new InvalidOperationException("ZNetPeer.m_socket is not an ISocket field.");
            var connection = AccessTools.DeclaredField(typeof(ZSteamSocket), "m_con");
            if (connection == null || connection.IsStatic || connection.FieldType != typeof(HSteamNetConnection))
                throw new InvalidOperationException("ZSteamSocket.m_con is not an HSteamNetConnection field.");
            Contract.Disconnect = AccessTools.DeclaredMethod(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
            if (Contract.Disconnect == null || Contract.Disconnect.IsStatic || Contract.Disconnect.ReturnType != typeof(void))
                throw new InvalidOperationException("ZNet.Disconnect(ZNetPeer) is not an instance void method.");
            if (!HasSetConfigValue(typeof(SteamNetworkingUtils)) || !HasSetConfigValue(typeof(SteamGameServerNetworkingUtils)))
                throw new InvalidOperationException("Steamworks SetConfigValue no longer has the expected signature.");
            RequireSendWindowShape(PatchProcessor.GetOriginalInstructions(Contract.SendZDOs), Contract.QueueSize);
            peerRef = AccessTools.FieldRefAccess<ZNetPeer>(Contract.Peer, "m_peer");
            connectionRef = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
            netPeersRef = AccessTools.FieldRefAccess<ZNet, List<ZNetPeer>>("m_peers");
        }

        internal static bool HasSetConfigValue(Type declaring)
        {
            var method = declaring.GetMethod("SetConfigValue", BindingFlags.Public | BindingFlags.Static, null,
                new[]
                {
                    typeof(ESteamNetworkingConfigValue), typeof(ESteamNetworkingConfigScope),
                    typeof(IntPtr), typeof(ESteamNetworkingConfigDataType), typeof(IntPtr)
                }, null);
            return method != null && method.ReturnType == typeof(bool);
        }

        // The rewrite assumes the exact vanilla gate: the queue size is read once, compared
        // against 10,240, and 10,240 is the batch budget it is subtracted from, with 2,048
        // as the minimum worth sending. Both constants become the peer's window; the 2,048
        // floor stays, so a peer is never handed a batch smaller than vanilla would send.
        internal static void RequireSendWindowShape(List<CodeInstruction> code, MethodInfo queueSize)
        {
            if (code.Count(IsWindowConstant) != 2)
                throw new InvalidOperationException("ZDOMan.SendZDOs no longer carries exactly two 10240 send-gate constants.");
            if (code.Count(instruction => IsConstant(instruction, 2048)) != 1)
                throw new InvalidOperationException("ZDOMan.SendZDOs no longer carries exactly one 2048 minimum-batch constant.");
            if (code.Count(instruction => instruction.Calls(queueSize)) != 1)
                throw new InvalidOperationException("ZDOMan.SendZDOs no longer reads the send queue size exactly once.");
        }

        private static bool IsWindowConstant(CodeInstruction instruction) => IsConstant(instruction, VanillaWindow);

        private static bool IsConstant(CodeInstruction instruction, int value) =>
            instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int operand && operand == value;

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            RequireSendWindowShape(code, Contract.QueueSize!);
            var window = AccessTools.DeclaredMethod(typeof(NetworkFlow), nameof(Window));
            var rewritten = new List<CodeInstruction>(code.Count + 2);
            foreach (var instruction in code)
            {
                if (!IsWindowConstant(instruction)) { rewritten.Add(instruction); continue; }
                // The peer argument replaces the constant in place, so the original
                // instruction's labels and exception blocks move to the first of the pair.
                var load = new CodeInstruction(OpCodes.Ldarg_1);
                load.labels.AddRange(instruction.labels);
                load.blocks.AddRange(instruction.blocks);
                rewritten.Add(load);
                rewritten.Add(new CodeInstruction(OpCodes.Call, window));
            }
            return rewritten;
        }

        // Called twice per SendZDOs with the ZDOPeer the native code was about to gate at
        // 10,240 B. Any doubt returns the vanilla constant rather than a guess.
        internal static int Window(object peer)
        {
            if (!Enabled) return VanillaWindow;
            try
            {
                var netPeer = peer == null ? null : peerRef?.Invoke(peer);
                if (netPeer == null) return VanillaWindow;
                long uid = netPeer.m_uid;
                if (uid == 0) return VanillaWindow;          // peer not ready; vanilla gate
                double now = Clock.Elapsed.TotalSeconds;
                lock (Gate)
                {
                    if (!Links.TryGetValue(uid, out Link? link)) { link = new Link(); Links[uid] = link; }
                    if (now - link.LastSampleSeconds < SampleIntervalSeconds) return Controller.Window(uid);
                    link.LastSampleSeconds = now;
                    if (!TryRead(netPeer, out SteamNetConnectionRealTimeStatus_t status)) { Fail(); return VanillaWindow; }
                    long pending = (long)status.m_cbPendingReliable + status.m_cbPendingUnreliable + status.m_cbSentUnackedReliable;
                    int bytes = Controller.Update(uid, status.m_nSendRateBytesPerSecond, status.m_nPing, pending, now);
                    ApplyRatePolicy(netPeer, link, status.m_nPing, now);
                    return bytes;
                }
            }
            catch { Fail(); return VanillaWindow; }
        }

        // A dedicated server holds a game-server Steam context; a listen-server host and a
        // client hold the ordinary client one. Asking the wrong interface returns a non-OK
        // EResult, which is a failed read rather than a zero measurement.
        private static bool TryRead(ZNetPeer peer, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = default(SteamNetConnectionRealTimeStatus_t);
            var socket = SteamTelemetry.SteamSocket(peer.m_socket);
            if (socket == null || connectionRef == null) return false;
            HSteamNetConnection connection = connectionRef(socket);
            var lane = default(SteamNetConnectionRealTimeLaneStatus_t);
            EResult response = Dedicated()
                ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane)
                : SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane);
            if (response != EResult.k_EResultOK) return false;
            ApplySendBuffer();
            return true;
        }

        private static bool Dedicated()
        {
            if (dedicatedKnown) return dedicated;
            var network = ZNet.instance;
            if (network == null) return false;
            dedicated = network.IsDedicated();
            dedicatedKnown = true;
            return dedicated;
        }

        // What BetterNetworking raised globally, once per process, on the first link that
        // answers: a 512 KB send buffer, so the window above is never capped by Steam's
        // own default queue.
        private static void ApplySendBuffer()
        {
            if (bufferApplied) return;
            bufferApplied = true;
            SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, SendBufferBytes);
        }

        // LAN links get a ceiling Steam will actually use; WAN links keep vanilla's floor
        // and gain the same ceiling, so congestion control decides instead of the pin.
        // Evaluated when a peer is first seen and every 30 s, applied only on a change.
        private static void ApplyRatePolicy(ZNetPeer peer, Link link, int pingMs, double now)
        {
            if (link.Class != LinkClass.Unknown && now - link.LastPolicySeconds < PolicyReviewSeconds) return;
            link.LastPolicySeconds = now;
            var socket = SteamTelemetry.SteamSocket(peer.m_socket);
            if (socket == null || connectionRef == null) return;
            HSteamNetConnection connection = connectionRef(socket);
            LinkClass measured = pingMs <= LanPingMs && !Relayed(connection) ? LinkClass.Lan : LinkClass.Wan;
            if (measured == link.Class) return;
            var scopeObject = (IntPtr)(long)connection.m_HSteamNetConnection;
            int minimum = measured == LinkClass.Lan ? LanRate : WanRateMin;
            int maximum = measured == LinkClass.Lan ? LanRate : WanRateMax;
            // Raise the ceiling before the floor so the two are never crossed in Steam.
            if (!SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, scopeObject, maximum) ||
                !SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, scopeObject, minimum))
            { Fail(); return; }
            link.Class = measured;
            Interlocked.Increment(ref rateSets);
        }

        // Path classification only, the same flags SteamTelemetry reads. No address or
        // identity is read or exported.
        private static bool Relayed(HSteamNetConnection connection)
        {
            SteamNetConnectionInfo_t info;
            bool described = Dedicated()
                ? SteamGameServerNetworkingSockets.GetConnectionInfo(connection, out info)
                : SteamNetworkingSockets.GetConnectionInfo(connection, out info);
            // An undescribed connection is treated as relayed: the LAN ceiling is the
            // riskier of the two, so it is never applied on a guess.
            if (!described) return true;
            return (info.m_nFlags & RelayedFlag) != 0 || (uint)info.m_idPOPRelay != 0;
        }

        private static bool SetInt(ESteamNetworkingConfigValue value, ESteamNetworkingConfigScope scope, IntPtr scopeObject, int argument)
        {
            GCHandle handle = GCHandle.Alloc(argument, GCHandleType.Pinned);
            try
            {
                return Dedicated()
                    ? SteamGameServerNetworkingUtils.SetConfigValue(value, scope, scopeObject,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, handle.AddrOfPinnedObject())
                    : SteamNetworkingUtils.SetConfigValue(value, scope, scopeObject,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, handle.AddrOfPinnedObject());
            }
            finally { handle.Free(); }
        }

        private static void AfterDisconnect(ZNetPeer peer)
        {
            if (!Installed || peer == null) return;
            try
            {
                lock (Gate)
                {
                    Links.Remove(peer.m_uid);
                    Controller.Forget(peer.m_uid);
                }
            }
            catch { Fail(); }
        }

        // A peer that left without passing through Disconnect is dropped here, so the
        // bounded controller is never held open by a dead uid.
        private static void ForgetDeparted()
        {
            var network = ZNet.instance;
            if (network == null || netPeersRef == null || Links.Count == 0) return;
            var peers = netPeersRef(network);
            if (peers == null) return;
            Expired.Clear();
            foreach (var entry in Links)
            {
                bool present = false;
                for (int i = 0; i < peers.Count && !present; i++)
                    if (peers[i] != null && peers[i].m_uid == entry.Key) present = true;
                if (!present) Expired.Add(entry.Key);
            }
            for (int i = 0; i < Expired.Count; i++) { Links.Remove(Expired[i]); Controller.Forget(Expired[i]); }
            Expired.Clear();
        }

        private static void Fail()
        {
            Interlocked.Increment(ref failures);
            if (Interlocked.Increment(ref failureTotal) < FailureLimit) return;
            // Do not unpatch from inside a hook: the flag alone returns the vanilla constant.
            failed = true;
            Status = "failed";
        }

        internal static void Reset()
        {
            lock (Gate) { Controller.Drain(); }
            Interlocked.Exchange(ref rateSets, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            SendWindowSummary summary;
            int lan = 0, wan = 0;
            lock (Gate)
            {
                // Best-effort sweep, not a hook: a runtime that cannot touch ZNet (the contract harness)
                // must not read as a failing module, so nothing is counted here.
                try { ForgetDeparted(); } catch { }
                summary = Controller.Drain();
                foreach (var entry in Links)
                {
                    if (entry.Value.Class == LinkClass.Lan) lan++;
                    else if (entry.Value.Class == LinkClass.Wan) wan++;
                }
            }
            gauges.Add(new NumberValue("net_flow_window_min_bytes", summary.WindowMin, "bytes"));
            gauges.Add(new NumberValue("net_flow_window_max_bytes", summary.WindowMax, "bytes"));
            gauges.Add(new NumberValue("net_flow_updates", summary.Updates, "updates"));
            gauges.Add(new NumberValue("net_flow_backoffs", summary.Backoffs, "backoffs"));
            gauges.Add(new NumberValue("net_flow_recoveries", summary.Recoveries, "recoveries"));
            gauges.Add(new NumberValue("net_flow_unknown_rate", summary.UnknownRate, "updates"));
            gauges.Add(new NumberValue("net_flow_peers", summary.Peers, "peers"));
            gauges.Add(new NumberValue("net_flow_peers_over_capacity", summary.PeersOverCapacity, "updates"));
            gauges.Add(new NumberValue("net_flow_lan_links", lan, "peers"));
            gauges.Add(new NumberValue("net_flow_wan_links", wan, "peers"));
            gauges.Add(new NumberValue("net_flow_rate_sets", Interlocked.Exchange(ref rateSets, 0), "changes"));
            gauges.Add(new NumberValue("net_flow_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("net_flow_status", !Installed ? Status : failed ? "failed" : Enabled ? "enabled" : "installed_disabled"));
            labels.Add(new TextValue("net_flow_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("net_flow_scope", "both_roles; window_replaces_10240_gate_in_SendZDOs; rate_pinned_on_lan_only"));
        }

        internal static void Uninstall()
        {
            Reset();
            try { Patches.UnpatchSelf(); } catch { }
            lock (Gate)
            {
                foreach (var entry in Links) Controller.Forget(entry.Key);
                Links.Clear();
                Expired.Clear();
            }
            Installed = false;
            failed = false;
            Status = "disabled";
            option = null;
            peerRef = null;
            connectionRef = null;
            netPeersRef = null;
            dedicatedKnown = false;
            dedicated = false;
            Interlocked.Exchange(ref failureTotal, 0);
        }

        // Only our own modules may sit on the two members the rewrite depends on; a foreign
        // owner means another mod is already shaping the same gate.
        private static string? ForeignOwner()
        {
            var guarded = new MethodBase?[]
            {
                Contract.SendZDOs,
                AccessTools.DeclaredMethod(typeof(ZSteamSocket), "GetSendQueueSize", Type.EmptyTypes),
            };
            foreach (var method in guarded)
            {
                if (method == null) continue;
                var owners = Harmony.GetPatchInfo(method)?.Owners;
                if (owners == null) continue;
                foreach (string owner in owners)
                    if (owner != Plugin.PluginId && !owner.StartsWith(OwnOwnerPrefix, StringComparison.Ordinal))
                        return owner;
            }
            return null;
        }
    }
}
