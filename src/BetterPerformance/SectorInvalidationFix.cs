using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Server-side re-issue of a native call, nothing else. ZDO.InternalSetPosition calls
    // SetSector before it stores m_position, so the peer invalidation that SetSector
    // triggers still reads the OLD position: a peer whose active area no longer contains
    // the object is never told, and keeps a stale instance. The postfix repeats
    // ZDOMan.ZDOSectorInvalidated once the new position is in place.
    //
    // Cost: the prefix and postfix run on every server-side position write (thousands per
    // second). The common path is one position read, one GetSectorIndex (integer math) and
    // one uint compare per hook, and it allocates nothing. The peer scan that measures
    // sector_fix_invalidations_added runs only on a jump over 64 m; it is the only path
    // that allocates (one list enumerator), besides failure logging.
    internal static class SectorInvalidationFix
    {
        private const int FailureLimit = 8;
        private const float JumpDistanceSquared = 64f * 64f;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".SectorInvalidationFix");
        private static ConfigEntry<bool>? option;
        private static AccessTools.FieldRef<ZDOMan, object>? peersRef;
        private static AccessTools.FieldRef<object, HashSet<ZDOID>>? invalidRef;
        private static bool failed;
        private static long sectorChanges, zoneJumps, invalidationsAdded, failures, failureTotal;

        internal static bool Installed { get; private set; }
        internal static bool Enabled => Installed && !failed && option != null && option.Value;
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            try
            {
                option = config.Bind("Replication", "SectorInvalidationFixEnabled", false,
                    "Server only. Re-issues the peer sector invalidation after a ZDO's position is written, so a teleported player or any object that jumps out of a peer's active area is removed on that peer at once instead of on its next sector crossing. Requires a restart.");
                // Off means no patch at all: the hooks sit on a hot path, and a disabled pair
                // would still cost two calls per position write for nothing.
                if (!option.Value) { Installed = false; Status = "disabled"; return; }
                string? superseded = Verify();
                if (superseded != null)
                {
                    Installed = false;
                    Status = superseded;
                    logger.LogInfo("Sector invalidation fix not installed: the game now writes the position before invalidating the sector.");
                    return;
                }
                Patches.Patch(Contract.SetPosition!,
                    prefix: new HarmonyMethod(typeof(SectorInvalidationFix), nameof(BeforePosition)),
                    postfix: new HarmonyMethod(typeof(SectorInvalidationFix), nameof(AfterPosition)));
                Installed = true;
                failed = false;
                Status = "installed";
                logger.LogInfo("Sector invalidation fix installed; the re-issued invalidation remains opt-in and server-side.");
            }
            catch (Exception exception)
            {
                Installed = false;
                Status = "unavailable_unexpected_shape";
                // A failed rollback must not escape: Installed=false already makes every
                // hook a no-op, and an escaping exception would abort plugin start-up.
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                logger.LogWarning("Sector invalidation fix unavailable; native invalidation retained: " + exception.Message);
            }
        }

        private static class Contract
        {
            internal static MethodInfo? SetPosition, SetSector, ManInvalidated, PeerInvalidated, GetPosition, InActiveArea, GetSectorIndex;
            internal static FieldInfo? Position, Peers, InvalidSector;
            internal static Type? Peer;
        }

        // Shape checks only, no IL fingerprint: the defect is an ordering property of
        // InternalSetPosition, and the order itself is what is asserted here. Every other
        // member the hooks touch is required to exist with the exact expected signature.
        // Returns a status when the game has fixed the order itself, otherwise null;
        // throws when the shape is unrecognised, so Install falls back to native behaviour.
        internal static string? Verify()
        {
            Contract.SetPosition = AccessTools.DeclaredMethod(typeof(ZDO), "InternalSetPosition", new[] { typeof(Vector3) });
            Contract.SetSector = AccessTools.DeclaredMethod(typeof(ZDO), "SetSector", new[] { typeof(ZoneSystem.SectorIndex) });
            Contract.ManInvalidated = AccessTools.DeclaredMethod(typeof(ZDOMan), "ZDOSectorInvalidated", new[] { typeof(ZDO) });
            Contract.GetPosition = AccessTools.DeclaredMethod(typeof(ZDO), "GetPosition", Type.EmptyTypes);
            Contract.InActiveArea = AccessTools.DeclaredMethod(typeof(ZNetScene), "InActiveArea", new[] { typeof(Vector3), typeof(Vector3) });
            Contract.GetSectorIndex = AccessTools.DeclaredMethod(typeof(ZoneSystem), "GetSectorIndex", new[] { typeof(Vector3) });
            Contract.Position = AccessTools.DeclaredField(typeof(ZDO), "m_position");
            Contract.Peer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
            if (Contract.SetPosition == null || Contract.SetPosition.IsStatic || !Contract.SetPosition.IsPublic || Contract.SetPosition.ReturnType != typeof(void))
                throw new InvalidOperationException("ZDO.InternalSetPosition(Vector3) is not a public instance void method.");
            if (Contract.SetSector == null || Contract.SetSector.IsStatic || Contract.SetSector.ReturnType != typeof(void))
                throw new InvalidOperationException("ZDO.SetSector(SectorIndex) is not an instance void method.");
            if (Contract.ManInvalidated == null || Contract.ManInvalidated.IsStatic || !Contract.ManInvalidated.IsPublic || Contract.ManInvalidated.ReturnType != typeof(void))
                throw new InvalidOperationException("ZDOMan.ZDOSectorInvalidated(ZDO) is not a public instance void method.");
            if (Contract.GetPosition == null || Contract.GetPosition.IsStatic || Contract.GetPosition.ReturnType != typeof(Vector3))
                throw new InvalidOperationException("ZDO.GetPosition() no longer returns the position.");
            if (Contract.InActiveArea == null || !Contract.InActiveArea.IsStatic || Contract.InActiveArea.ReturnType != typeof(bool))
                throw new InvalidOperationException("ZNetScene.InActiveArea(Vector3, Vector3) is not a public static bool.");
            if (Contract.GetSectorIndex == null || !Contract.GetSectorIndex.IsStatic || Contract.GetSectorIndex.ReturnType != typeof(ZoneSystem.SectorIndex))
                throw new InvalidOperationException("ZoneSystem.GetSectorIndex(Vector3) is not a public static SectorIndex.");
            if (Contract.Position == null || Contract.Position.FieldType != typeof(Vector3))
                throw new InvalidOperationException("ZDO.m_position is not a Vector3 field.");
            if (Contract.Peer == null)
                throw new InvalidOperationException("ZDOMan.ZDOPeer no longer exists.");
            Contract.PeerInvalidated = AccessTools.DeclaredMethod(Contract.Peer, "ZDOSectorInvalidated", new[] { typeof(ZDO) });
            Contract.Peers = AccessTools.DeclaredField(typeof(ZDOMan), "m_peers");
            Contract.InvalidSector = AccessTools.DeclaredField(Contract.Peer, "m_invalidSector");
            if (Contract.PeerInvalidated == null || Contract.PeerInvalidated.IsStatic || Contract.PeerInvalidated.ReturnType != typeof(void))
                throw new InvalidOperationException("ZDOPeer.ZDOSectorInvalidated(ZDO) is not an instance void method.");
            if (Contract.Peers == null || !typeof(IEnumerable).IsAssignableFrom(Contract.Peers.FieldType) ||
                !Contract.Peers.FieldType.IsGenericType || Contract.Peers.FieldType.GetGenericArguments()[0] != Contract.Peer)
                throw new InvalidOperationException("ZDOMan.m_peers is not an enumerable of ZDOPeer.");
            if (Contract.InvalidSector == null || Contract.InvalidSector.FieldType != typeof(HashSet<ZDOID>))
                throw new InvalidOperationException("ZDOPeer.m_invalidSector is not a HashSet<ZDOID>.");
            RequirePeerReadsPosition(PatchProcessor.GetOriginalInstructions(Contract.PeerInvalidated), Contract.GetPosition, Contract.InActiveArea);
            RequireSectorInvalidation(PatchProcessor.GetOriginalInstructions(Contract.SetSector), Contract.ManInvalidated);
            if (!SetsSectorBeforePosition(PatchProcessor.GetOriginalInstructions(Contract.SetPosition), Contract.SetSector, Contract.Position))
                return "superseded_by_game";
            peersRef = AccessTools.FieldRefAccess<ZDOMan, object>("m_peers");
            invalidRef = AccessTools.FieldRefAccess<HashSet<ZDOID>>(Contract.Peer, "m_invalidSector");
            return null;
        }

        // True while the defect is present: SetSector, and therefore the peer invalidation
        // it triggers, runs before the new position is stored.
        internal static bool SetsSectorBeforePosition(List<CodeInstruction> code, MethodInfo setSector, FieldInfo position)
        {
            int call = code.FindIndex(instruction => instruction.Calls(setSector));
            int store = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, position));
            if (call < 0 || store < 0)
                throw new InvalidOperationException("ZDO.InternalSetPosition no longer both calls SetSector and stores m_position.");
            return call < store;
        }

        internal static void RequireSectorInvalidation(List<CodeInstruction> code, MethodInfo invalidated)
        {
            if (!code.Exists(instruction => instruction.Calls(invalidated)))
                throw new InvalidOperationException("ZDO.SetSector no longer calls ZDOMan.ZDOSectorInvalidated.");
        }

        // The whole fix rests on the peer test reading the ZDO position: if it stopped
        // doing so, re-issuing the call after the write would change nothing.
        internal static void RequirePeerReadsPosition(List<CodeInstruction> code, MethodInfo getPosition, MethodInfo inActiveArea)
        {
            if (!code.Exists(instruction => instruction.Calls(getPosition)) || !code.Exists(instruction => instruction.Calls(inActiveArea)))
                throw new InvalidOperationException("ZDOPeer.ZDOSectorInvalidated no longer tests the ZDO position against the peer area.");
        }

        internal struct PositionState
        {
            internal bool Watch;
            internal Vector3 Position;
            internal uint Sector;
        }

        private static void BeforePosition(ZDO __instance, out PositionState __state)
        {
            __state = default;
            if (!Enabled || __instance == null) return;
            try
            {
                var net = ZNet.instance;
                if (net == null || !net.IsServer() || ZDOMan.instance == null) return;
                Vector3 position = __instance.GetPosition();
                __state.Watch = true;
                __state.Position = position;
                __state.Sector = ZoneSystem.GetSectorIndex(position).Sector;
            }
            catch { Fail(); __state = default; }
        }

        private static void AfterPosition(ZDO __instance, PositionState __state)
        {
            if (!__state.Watch || !Enabled || __instance == null) return;
            try
            {
                Vector3 position = __instance.GetPosition();
                uint sector = ZoneSystem.GetSectorIndex(position).Sector;
                if (sector == __state.Sector) return;
                var manager = ZDOMan.instance;
                if (manager == null) return;
                // Mirror the native early-out: SetSector leaves portal ZDOs in their sector,
                // so invalidating them here would drop an object the server never resends.
                var game = Game.instance;
                if (game != null && game.PortalPrefabHash.Contains(__instance.GetPrefab())) return;
                Interlocked.Increment(ref sectorChanges);
                float dx = position.x - __state.Position.x, dz = position.z - __state.Position.z;
                if (dx * dx + dz * dz <= JumpDistanceSquared)
                {
                    manager.ZDOSectorInvalidated(__instance);
                    return;
                }
                Interlocked.Increment(ref zoneJumps);
                long before = PendingInvalidations();
                manager.ZDOSectorInvalidated(__instance);
                long after = PendingInvalidations();
                if (before >= 0 && after > before) Interlocked.Add(ref invalidationsAdded, after - before);
            }
            catch { Fail(); }
        }

        // Jump path only. Returns -1 when the peer list cannot be read, so a missing
        // measurement is never counted as zero added invalidations.
        private static long PendingInvalidations()
        {
            var manager = ZDOMan.instance;
            if (manager == null || peersRef == null || invalidRef == null) return -1;
            if (!(peersRef(manager) is IEnumerable peers)) return -1;
            long pending = 0;
            foreach (object peer in peers)
            {
                if (peer == null) continue;
                var invalid = invalidRef(peer);
                if (invalid != null) pending += invalid.Count;
            }
            return pending;
        }

        private static void Fail()
        {
            Interlocked.Increment(ref failures);
            if (Interlocked.Increment(ref failureTotal) < FailureLimit) return;
            // Do not unpatch from inside a patch: the flag alone makes every hook a no-op.
            failed = true;
            Status = "failed";
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref sectorChanges, 0);
            Interlocked.Exchange(ref zoneJumps, 0);
            Interlocked.Exchange(ref invalidationsAdded, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("sector_fix_sector_changes", Interlocked.Exchange(ref sectorChanges, 0), "changes"));
            gauges.Add(new NumberValue("sector_fix_zone_jumps", Interlocked.Exchange(ref zoneJumps, 0), "jumps"));
            gauges.Add(new NumberValue("sector_fix_invalidations_added", Interlocked.Exchange(ref invalidationsAdded, 0), "ids"));
            gauges.Add(new NumberValue("sector_fix_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("sector_fix_status", !Installed ? Status : failed ? "failed" : Enabled ? "enabled" : "installed_disabled"));
            labels.Add(new TextValue("sector_fix_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("sector_fix_scope", "server_only; reissues_native_invalidation_after_position_write; client_unchanged"));
        }

        internal static void Uninstall()
        {
            Reset();
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            failed = false;
            Status = "disabled";
            option = null;
            peersRef = null;
            invalidRef = null;
            Interlocked.Exchange(ref failureTotal, 0);
        }
    }
}
