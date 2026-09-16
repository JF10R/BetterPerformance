using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Attribution for Heightmap.Regenerate. TimingHooks already measures its elapsed
    // time as Metric.HeightmapRegenerate; nothing here times anything. Prefixes on the
    // callers that can reach a regeneration set a thread-static context, and the
    // Regenerate prefix reads it to decide which counter to advance. Precedence is
    // terrain operation, then zone spawn, then component enable, then other: a ghost
    // zone spawn enables its heightmap inside SpawnZone, and the zone label is the
    // useful one there.
    internal static class TerrainTelemetry
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".TerrainTelemetry");
        private static int ownerThread;

        [ThreadStatic] private static int operationDepth, zoneDepth, enableDepth, doOperationDepth;
        [ThreadStatic] private static bool zoneGhost;
        [ThreadStatic] private static List<TerrainComp>? operationSaves;

        private static long regenTerrainOp, regenZoneFull, regenZoneGhost, regenEnable, regenOther;
        private static long framesWith1, framesWith23, framesWith4Plus;
        private static long operations, rpcDispatches, neighbourTotal;
        private static long otherThreadSkips, probeFailures;
        private static int regenFrame = -1, regenInFrame, regenMaxPerFrame;
        private static int operationFrame = -1, operationsInFrame, operationsMaxPerFrame;
        private static int neighbourMax;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct ScopeState { internal bool Entered; internal bool PreviousGhost; }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            if (!config.Bind("Diagnostics", "TerrainRegenerationTelemetry", true,
                "Attribute each Heightmap.Regenerate to the caller that requested it and count regenerations and terrain operations per frame. " +
                "Counting only; regeneration timing stays with the existing HeightmapRegenerate metric. Requires restart.").Value)
            { Status = "disabled"; return; }
            try
            {
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                // Heightmap implements IMonoUpdater, which a standalone CLR cannot
                // type-load, so it is resolved by name and never named in a signature.
                Type heightmap = typeof(ZNet).Assembly.GetType("Heightmap", false)
                    ?? throw new InvalidOperationException("Heightmap is unavailable.");
                Patch(heightmap, "Regenerate", Type.EmptyTypes, nameof(BeforeRegenerate), null);
                Patch(heightmap, "OnEnable", Type.EmptyTypes, nameof(BeforeEnable), nameof(AfterEnable));
                Patch(typeof(ZoneSystem), "SpawnZone",
                    new[] { typeof(Vector2s), typeof(ZoneSystem.SpawnMode), typeof(GameObject).MakeByRefType() },
                    nameof(BeforeSpawnZone), nameof(AfterSpawnZone), typeof(bool));
                Patch(typeof(TerrainComp), "RPC_ApplyOperation", new[] { typeof(long), typeof(ZPackage) },
                    nameof(BeforeRpc), nameof(AfterRpc));
                MethodInfo doOperation = ByShape("DoOperation", 3)
                    ?? throw new InvalidOperationException("TerrainComp.DoOperation is unavailable.");
                Patches.Patch(doOperation,
                    prefix: new HarmonyMethod(typeof(TerrainTelemetry), nameof(BeforeDoOperation)),
                    finalizer: new HarmonyMethod(typeof(TerrainTelemetry), nameof(AfterDoOperation)));
                MethodInfo save = AccessTools.DeclaredMethod(typeof(TerrainComp), "Save", new[] { typeof(bool) })
                    ?? throw new InvalidOperationException("TerrainComp.Save is unavailable.");
                Patches.Patch(save, prefix: new HarmonyMethod(typeof(TerrainTelemetry), nameof(BeforeSave)));
                Installed = Enabled = true;
                Status = "installed";
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                Release();
                logger.LogWarning("Terrain regeneration attribution unavailable: " + exception.GetType().Name);
            }
        }

        private static void Patch(Type type, string name, Type[] arguments, string prefix, string? finalizer,
            Type? returnType = null)
        {
            MethodInfo? method = AccessTools.DeclaredMethod(type, name, arguments);
            if (method == null || method.IsStatic || method.ReturnType != (returnType ?? typeof(void)))
                throw new InvalidOperationException("Unsupported terrain signature: " + type.Name + "." + name);
            Patches.Patch(method, prefix: new HarmonyMethod(typeof(TerrainTelemetry), prefix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(TerrainTelemetry), finalizer));
        }

        // DoOperation takes a nested game settings type; match on shape so resolution
        // never forces that type to load.
        private static MethodInfo? ByShape(string name, int parameters)
        {
            foreach (MethodInfo candidate in typeof(TerrainComp).GetMethods(Declared))
            {
                if (candidate.Name != name || candidate.IsStatic) continue;
                try { if (candidate.ReturnType == typeof(void) && candidate.GetParameters().Length == parameters) return candidate; }
                catch { continue; }
            }
            return null;
        }

        private static bool Foreign()
        {
            if (Thread.CurrentThread.ManagedThreadId == ownerThread) return false;
            Interlocked.Increment(ref otherThreadSkips);
            return true;
        }

        private static void BeforeRegenerate()
        {
            if (!Enabled || Foreign()) return;
            try
            {
                if (operationDepth > 0) Interlocked.Increment(ref regenTerrainOp);
                else if (zoneDepth > 0 && zoneGhost) Interlocked.Increment(ref regenZoneGhost);
                else if (zoneDepth > 0) Interlocked.Increment(ref regenZoneFull);
                else if (enableDepth > 0) Interlocked.Increment(ref regenEnable);
                else Interlocked.Increment(ref regenOther);
                int frame = Time.frameCount;
                if (frame != regenFrame) { Commit(regenInFrame); regenFrame = frame; regenInFrame = 0; }
                regenInFrame++;
                if (regenInFrame > regenMaxPerFrame) regenMaxPerFrame = regenInFrame;
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static void Commit(int count)
        {
            if (count <= 0) return;
            if (count == 1) Interlocked.Increment(ref framesWith1);
            else if (count <= 3) Interlocked.Increment(ref framesWith23);
            else Interlocked.Increment(ref framesWith4Plus);
        }

        private static void BeforeEnable(out ScopeState __state)
        {
            __state = new ScopeState { Entered = Enabled, PreviousGhost = zoneGhost };
            if (__state.Entered) enableDepth++;
        }

        private static void AfterEnable(ScopeState __state)
        {
            if (__state.Entered && enableDepth > 0) enableDepth--;
        }

        private static void BeforeSpawnZone(ZoneSystem.SpawnMode __1, out ScopeState __state)
        {
            __state = new ScopeState { Entered = Enabled, PreviousGhost = zoneGhost };
            if (!__state.Entered) return;
            zoneDepth++;
            zoneGhost = __1 == ZoneSystem.SpawnMode.Ghost;
        }

        private static void AfterSpawnZone(ScopeState __state)
        {
            if (!__state.Entered) return;
            if (zoneDepth > 0) zoneDepth--;
            zoneGhost = __state.PreviousGhost;
        }

        private static void BeforeRpc(out ScopeState __state)
        {
            __state = new ScopeState { Entered = Enabled, PreviousGhost = zoneGhost };
            if (!__state.Entered) return;
            operationDepth++;
            Interlocked.Increment(ref rpcDispatches);
        }

        private static void AfterRpc(ScopeState __state)
        {
            if (__state.Entered && operationDepth > 0) operationDepth--;
        }

        // The executed operation. RPC_ApplyOperation also runs on non-owners, where it
        // returns without doing work; DoOperation is entered only when work happens.
        private static void BeforeDoOperation(out ScopeState __state)
        {
            __state = new ScopeState { Entered = Enabled, PreviousGhost = zoneGhost };
            if (!__state.Entered) return;
            operationDepth++;
            if (++doOperationDepth != 1) return;
            Interlocked.Increment(ref operations);
            (operationSaves ?? (operationSaves = new List<TerrainComp>(8))).Clear();
            if (Foreign()) return;
            try
            {
                int frame = Time.frameCount;
                if (frame != operationFrame) { operationFrame = frame; operationsInFrame = 0; }
                operationsInFrame++;
                if (operationsInFrame > operationsMaxPerFrame) operationsMaxPerFrame = operationsInFrame;
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static void AfterDoOperation(ScopeState __state)
        {
            if (!__state.Entered) return;
            if (operationDepth > 0) operationDepth--;
            if (doOperationDepth <= 0 || --doOperationDepth != 0) return;
            int touched = operationSaves?.Count ?? 0;
            Interlocked.Add(ref neighbourTotal, touched);
            if (touched > neighbourMax) neighbourMax = touched;
            operationSaves?.Clear();
        }

        // Distinct compilers whose Save the operation reached, including the operation's
        // own compiler. Counting instances rather than calls keeps the number comparable
        // whether or not neighbour saves are coalesced.
        private static void BeforeSave(TerrainComp __instance)
        {
            if (!Enabled || doOperationDepth <= 0) return;
            var list = operationSaves;
            if (list == null || list.Count >= 32) return;
            if (!list.Contains(__instance)) list.Add(__instance);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            long with1 = Interlocked.Read(ref framesWith1), with23 = Interlocked.Read(ref framesWith23),
                with4 = Interlocked.Read(ref framesWith4Plus);
            int open = regenInFrame;
            if (open == 1) with1++; else if (open > 1 && open <= 3) with23++; else if (open > 3) with4++;
            gauges.Add(new NumberValue("heightmap_regen_terrain_op", Interlocked.Read(ref regenTerrainOp), "regenerations"));
            gauges.Add(new NumberValue("heightmap_regen_zone_spawn_full", Interlocked.Read(ref regenZoneFull), "regenerations"));
            gauges.Add(new NumberValue("heightmap_regen_zone_spawn_ghost", Interlocked.Read(ref regenZoneGhost), "regenerations"));
            gauges.Add(new NumberValue("heightmap_regen_enable", Interlocked.Read(ref regenEnable), "regenerations"));
            gauges.Add(new NumberValue("heightmap_regen_other", Interlocked.Read(ref regenOther), "regenerations"));
            gauges.Add(new NumberValue("heightmap_regen_frames_with_1", with1, "frames"));
            gauges.Add(new NumberValue("heightmap_regen_frames_with_2_3", with23, "frames"));
            gauges.Add(new NumberValue("heightmap_regen_frames_with_4_plus", with4, "frames"));
            gauges.Add(new NumberValue("heightmap_regen_max_per_frame", regenMaxPerFrame, "regenerations"));
            gauges.Add(new NumberValue("terrain_ops_total", Interlocked.Read(ref operations), "operations"));
            gauges.Add(new NumberValue("terrain_op_rpc_dispatches_total", Interlocked.Read(ref rpcDispatches), "calls"));
            gauges.Add(new NumberValue("terrain_ops_per_frame_max", operationsMaxPerFrame, "operations"));
            gauges.Add(new NumberValue("terrain_op_neighbours_max", neighbourMax, "compilers"));
            gauges.Add(new NumberValue("terrain_op_neighbours_total", Interlocked.Read(ref neighbourTotal), "compilers"));
            gauges.Add(new NumberValue("heightmap_regen_other_thread_skips", Interlocked.Read(ref otherThreadSkips), "calls"));
            gauges.Add(new NumberValue("heightmap_regen_probe_failures", Interlocked.Read(ref probeFailures), "calls"));
            labels.Add(new TextValue("terrain_regen_scope",
                "main_thread_caller_context; precedence terrain_op>zone_spawn>enable>other"));
            labels.Add(new TextValue("terrain_telemetry_status", Status));
            labels.Add(new TextValue("terrain_op_neighbours_semantics",
                "distinct TerrainComp instances saved during one DoOperation, including the operation's own compiler"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref regenTerrainOp, 0);
            Interlocked.Exchange(ref regenZoneFull, 0);
            Interlocked.Exchange(ref regenZoneGhost, 0);
            Interlocked.Exchange(ref regenEnable, 0);
            Interlocked.Exchange(ref regenOther, 0);
            Interlocked.Exchange(ref framesWith1, 0);
            Interlocked.Exchange(ref framesWith23, 0);
            Interlocked.Exchange(ref framesWith4Plus, 0);
            Interlocked.Exchange(ref operations, 0);
            Interlocked.Exchange(ref rpcDispatches, 0);
            Interlocked.Exchange(ref neighbourTotal, 0);
            Interlocked.Exchange(ref otherThreadSkips, 0);
            Interlocked.Exchange(ref probeFailures, 0);
            regenFrame = -1; regenInFrame = 0; regenMaxPerFrame = 0;
            operationFrame = -1; operationsInFrame = 0; operationsMaxPerFrame = 0;
            neighbourMax = 0;
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Installed = false;
            Status = "disabled";
            operationDepth = zoneDepth = enableDepth = doOperationDepth = 0;
            operationSaves?.Clear();
            Release();
        }

        // Harmony rescans every patched method when it removes a patch, so a shared
        // standalone CLR can refuse the removal for an unrelated Unity type.
        private static void Release()
        {
            try { Patches.UnpatchSelf(); }
            catch (Exception exception) { Status = Status + "_unpatch_failed_" + exception.GetType().Name; }
        }
    }
}
