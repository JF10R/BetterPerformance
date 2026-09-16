using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Server-side send-queue reordering only. When an incoming ZDO update applied by
    // ZDOMan.RPC_ZDOData changes a ZDO's owner to a connected peer other than the
    // sender, that peer's next sync list receives the ZDO at index 0 through the
    // native ForceSendZDO/AddForceSendZdos path, the same mechanism Container's
    // RPC_RequestOpen already uses. No data, ownership, destruction or save order
    // changes: the owner was already assigned by the native handler before the hook
    // runs, and AddForceSendZdos still applies the native ZDOPeer.ShouldSend revision
    // test, so a peer that already holds the revision is never sent to.
    internal static class OwnershipExpedite
    {
        private const int WindowMilliseconds = 1000, DedupeLimit = 4096;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".OwnershipExpedite");
        private static readonly Stopwatch Clock = new Stopwatch();
        private static readonly HashSet<Forced> Window = new HashSet<Forced>();
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<int>? perSecond;
        private static int ownerThread;
        private static long windowStart, windowCount;
        private static long observed, expedited, skippedSender, skippedOffline, skippedUnchanged, skippedCapacity, skippedDuplicate;
        private static long otherThreadSkips, failures;
        [ThreadStatic] private static bool incoming;
        [ThreadStatic] private static long incomingSender;

        internal static bool Installed { get; private set; }
        internal static bool Enabled => Installed && option != null && option.Value;
        internal static string Status { get; private set; } = "disabled";

        // Observation is always installed so the grant counters exist for the A/B;
        // only the forced insert itself is gated by the opt-in, default-off key.
        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            try
            {
                option = config.Bind("Ownership", "ExpediteOwnerGrantsEnabled", false,
                    "Server only. When an incoming ZDO update grants ownership to another connected peer, queue that ZDO first for the new owner using the native forced-send path. Reordering only; no data, ownership or save change. Counters stay on when this is off.");
                perSecond = config.Bind("Ownership", "ExpediteMaxPerSecond", 256,
                    new ConfigDescription("Upper bound on forced inserts per second. Excess grants are counted and left to the ordinary queue.",
                        new AcceptableValueRange<int>(1, 4096)));
                ValidateContracts();
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                Clock.Restart();
                windowStart = 0;
                Patches.Patch(Contract.Incoming!,
                    prefix: new HarmonyMethod(typeof(OwnershipExpedite), nameof(BeforeIncoming)),
                    finalizer: new HarmonyMethod(typeof(OwnershipExpedite), nameof(AfterIncoming)));
                Patches.Patch(Contract.SetOwnerInternal!,
                    prefix: new HarmonyMethod(typeof(OwnershipExpedite), nameof(BeforeSetOwner)),
                    postfix: new HarmonyMethod(typeof(OwnershipExpedite), nameof(AfterSetOwner)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Owner-grant expedite observers installed; forced sends remain opt-in and server-side.");
            }
            catch (Exception exception)
            {
                Installed = false;
                Status = "unavailable";
                // A failed rollback must not escape: Installed=false already makes every
                // hook a no-op, and an escaping exception would abort plugin start-up.
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                logger.LogWarning("Owner-grant expedite unavailable; native send ordering retained: " + exception.GetType().Name);
            }
        }

        private static class Contract
        {
            internal static System.Reflection.MethodInfo? Incoming, SetOwnerInternal, ForceSend, GetOwner, SessionId, GetPeer;
        }

        // A strict shape check on the exact owner-apply site. RPC_ZDOData applies an
        // incoming owner through SetOwnerInternal at two points: the owner-revision-only
        // branch and the full-update branch. Any other count means the handler changed
        // and the observation is no longer sound, so fall back to native behaviour.
        private static void ValidateContracts()
        {
            Contract.Incoming = AccessTools.DeclaredMethod(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
            Contract.SetOwnerInternal = AccessTools.DeclaredMethod(typeof(ZDO), "SetOwnerInternal", new[] { typeof(long) });
            Contract.ForceSend = AccessTools.DeclaredMethod(typeof(ZDOMan), "ForceSendZDO", new[] { typeof(long), typeof(ZDOID) });
            Contract.GetOwner = AccessTools.DeclaredMethod(typeof(ZDO), "GetOwner", Type.EmptyTypes);
            Contract.SessionId = AccessTools.DeclaredMethod(typeof(ZDOMan), "GetSessionID", Type.EmptyTypes);
            Contract.GetPeer = AccessTools.DeclaredMethod(typeof(ZNet), "GetPeer", new[] { typeof(long) });
            if (Contract.Incoming == null || Contract.Incoming.IsStatic || Contract.Incoming.ReturnType != typeof(void) ||
                Contract.SetOwnerInternal == null || Contract.SetOwnerInternal.IsStatic || Contract.SetOwnerInternal.ReturnType != typeof(void) ||
                Contract.ForceSend == null || Contract.ForceSend.IsStatic || Contract.ForceSend.ReturnType != typeof(void) ||
                Contract.GetOwner == null || Contract.GetOwner.IsStatic || Contract.GetOwner.ReturnType != typeof(long) ||
                Contract.SessionId == null || !Contract.SessionId.IsStatic || Contract.SessionId.ReturnType != typeof(long) ||
                Contract.GetPeer == null || Contract.GetPeer.IsStatic || Contract.GetPeer.ReturnType != typeof(ZNetPeer))
                throw new InvalidOperationException("Unsupported ownership grant signatures.");
            var rpcField = AccessTools.DeclaredField(typeof(ZNetPeer), "m_rpc");
            var uidField = AccessTools.DeclaredField(typeof(ZNetPeer), "m_uid");
            if (rpcField?.FieldType != typeof(ZRpc) || uidField?.FieldType != typeof(long))
                throw new InvalidOperationException("Unsupported peer identity fields.");
            RequireApplySites(PatchProcessor.GetOriginalInstructions(Contract.Incoming), Contract.SetOwnerInternal);
        }

        internal static void RequireApplySites(IEnumerable<CodeInstruction> code, System.Reflection.MethodInfo apply)
        {
            int applies = code.Count(instruction => instruction.Calls(apply));
            if (applies != 2) throw new InvalidOperationException("Incoming owner-apply contract changed: " + applies + " apply sites.");
        }

        internal struct IncomingState { internal bool Previous; internal long PreviousSender; }
        internal struct OwnerState { internal bool Observe; internal long Previous; }

        private static void BeforeIncoming(ZRpc rpc, out IncomingState __state)
        {
            __state = new IncomingState { Previous = incoming, PreviousSender = incomingSender };
            incoming = false;
            incomingSender = 0;
            if (!Installed) return;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread) { Interlocked.Increment(ref otherThreadSkips); return; }
            try
            {
                var net = ZNet.instance;
                if (net == null || !net.IsServer() || ZDOMan.instance == null) return;
                incomingSender = Sender(net, rpc);
                incoming = true;
            }
            catch { Interlocked.Increment(ref failures); incoming = false; }
        }

        private static void AfterIncoming(IncomingState __state)
        {
            incoming = __state.Previous;
            incomingSender = __state.PreviousSender;
        }

        private static long Sender(ZNet net, ZRpc rpc)
        {
            if (rpc == null) return 0;
            var peers = net.GetPeers();
            if (peers == null) return 0;
            foreach (var peer in peers)
                if (peer != null && ReferenceEquals(peer.m_rpc, rpc)) return peer.m_uid;
            return 0;
        }

        private static void BeforeSetOwner(ZDO __instance, out OwnerState __state)
        {
            __state = default;
            if (!incoming || __instance == null) return;
            try { __state = new OwnerState { Observe = true, Previous = __instance.GetOwner() }; }
            catch { Interlocked.Increment(ref failures); }
        }

        private static void AfterSetOwner(ZDO __instance, long uid, OwnerState __state)
        {
            if (!__state.Observe || __instance == null) return;
            try
            {
                if (__state.Previous == uid) { Interlocked.Increment(ref skippedUnchanged); return; }
                Interlocked.Increment(ref observed);
                if (incomingSender == 0 || uid == incomingSender) { Interlocked.Increment(ref skippedSender); return; }
                if (uid == 0 || uid == ZDOMan.GetSessionID() || ZNet.instance?.GetPeer(uid) == null)
                { Interlocked.Increment(ref skippedOffline); return; }
                if (!Enabled) return;
                if (!Admit(uid, __instance.m_uid)) return;
                // Native path: ZDOMan.ForceSendZDO(peerID, id) resolves the server-side
                // ZDOPeer and adds the id to m_forceSend. AddForceSendZdos then inserts
                // it at index 0 only while ZDOPeer.ShouldSend still holds.
                ZDOMan.instance.ForceSendZDO(uid, __instance.m_uid);
                Interlocked.Increment(ref expedited);
            }
            catch { Interlocked.Increment(ref failures); }
        }

        // Bounded on a module-local clock, not on the capture poll: the cap must hold
        // whether or not a capture is recording.
        private static bool Admit(long peer, ZDOID id)
        {
            long now = Clock.ElapsedMilliseconds;
            if (now - windowStart >= WindowMilliseconds)
            {
                windowStart = now;
                windowCount = 0;
                Window.Clear();
            }
            int limit = perSecond?.Value ?? 0;
            if (limit < 1 || windowCount >= limit || Window.Count >= DedupeLimit)
            { Interlocked.Increment(ref skippedCapacity); return false; }
            if (!Window.Add(new Forced(peer, id))) { Interlocked.Increment(ref skippedDuplicate); return false; }
            windowCount++;
            return true;
        }

        private readonly struct Forced : IEquatable<Forced>
        {
            private readonly long peer;
            private readonly ZDOID id;
            internal Forced(long peer, ZDOID id) { this.peer = peer; this.id = id; }
            public bool Equals(Forced other) => peer == other.peer && id == other.id;
            public override bool Equals(object? obj) => obj is Forced other && Equals(other);
            public override int GetHashCode() => unchecked(peer.GetHashCode() * 397 ^ id.GetHashCode());
        }

        internal static void Reset()
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            Window.Clear();
            windowCount = 0;
            windowStart = Clock.ElapsedMilliseconds;
            Interlocked.Exchange(ref observed, 0);
            Interlocked.Exchange(ref expedited, 0);
            Interlocked.Exchange(ref skippedSender, 0);
            Interlocked.Exchange(ref skippedOffline, 0);
            Interlocked.Exchange(ref skippedUnchanged, 0);
            Interlocked.Exchange(ref skippedCapacity, 0);
            Interlocked.Exchange(ref skippedDuplicate, 0);
            Interlocked.Exchange(ref otherThreadSkips, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("ownership_grants_observed", Interlocked.Exchange(ref observed, 0), "grants"));
            gauges.Add(new NumberValue("ownership_grants_expedited", Interlocked.Exchange(ref expedited, 0), "grants"));
            gauges.Add(new NumberValue("ownership_grants_skipped_sender", Interlocked.Exchange(ref skippedSender, 0), "grants"));
            gauges.Add(new NumberValue("ownership_grants_skipped_offline", Interlocked.Exchange(ref skippedOffline, 0), "grants"));
            gauges.Add(new NumberValue("ownership_grants_skipped_unchanged", Interlocked.Exchange(ref skippedUnchanged, 0), "calls"));
            gauges.Add(new NumberValue("ownership_grants_skipped_capacity", Interlocked.Exchange(ref skippedCapacity, 0), "grants"));
            gauges.Add(new NumberValue("ownership_grants_skipped_duplicate", Interlocked.Exchange(ref skippedDuplicate, 0), "grants"));
            gauges.Add(new NumberValue("ownership_expedite_other_thread_skips", Interlocked.Exchange(ref otherThreadSkips, 0), "calls"));
            gauges.Add(new NumberValue("ownership_expedite_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("ownership_expedite_status", !Installed ? Status : Enabled ? "enabled" : "installed_disabled"));
            labels.Add(new TextValue("ownership_expedite_scope", "server_side_send_order_only; observed_counts_incoming_owner_changes_not_latency"));
        }

        internal static void Uninstall()
        {
            Reset();
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            Status = "disabled";
            option = null;
            perSecond = null;
            Window.Clear();
            windowCount = 0;
            Clock.Reset();
        }
    }
}
