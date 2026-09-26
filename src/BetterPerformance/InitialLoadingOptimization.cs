using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Initial remote-client spawn and, since 0.4.18, a distant teleport's loading screen after the move
    // (TeleportZonePreparation). All actual zone construction remains native.
    internal static class InitialLoadingOptimization
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".InitialLoading");
        private static readonly MethodInfo Create = AccessTools.DeclaredMethod(typeof(ZoneSystem), "CreateLocalZones", new[] { typeof(Vector3) });
        private static readonly MethodInfo Poke = AccessTools.DeclaredMethod(typeof(ZoneSystem), "PokeLocalZone", new[] { typeof(Vector2s) });
        private static readonly MethodInfo Update = AccessTools.DeclaredMethod(typeof(ZoneSystem), "Update", Type.EmptyTypes);
        private static readonly MethodInfo[] GuardedMethods = { Update, Create, Poke };
        private static ConfigEntry<bool>? enabled;
        private static Func<ZoneSystem, Vector3, bool> native = null!;
        private static AccessTools.FieldRef<Game, bool> firstSpawn = null!;
        private static int ownerThread;
        private static bool inCall;
        private static WeakReference? episodeGame;
        private static long sequence, calls, firstSuccesses, extraCalls, extraSuccesses, noProgress, budgetStops, capStops, contextStops, failures;
        private static double totalMs, maxMs, extraMs, extraMaxMs;
        // Teleport bursts: per-interval counters, taken by Sample.
        private const int TeleportMaxPasses = 16;
        private static long teleportCalls, teleportExtraSuccesses, teleportBudgetStops, teleportNoProgress, teleportCapStops;
        private static double teleportMaxMs;
        private static string startedUtc = "unobserved";
        internal static string Status { get; private set; } = "disabled_at_startup";
        internal static bool Installed { get; private set; }
        internal static bool Enabled => Installed && enabled != null && enabled.Value && Status == "installed";
        internal static bool TeleportEnabled => Installed && Status == "installed" && TeleportZonePreparation.BurstEnabled;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            enabled = config.Bind("InitialLoading", "Enabled", false,
                "Experimental faster initial join to a remote server. Up to four native zone passes with a soft 8 ms budget; may increase loading frame cost. Set false and restart the client to disable. Does not accelerate dedicated/listen servers or deaths; teleports have [Teleport] ZoneBurstEnabled.");
            if (!enabled.Value && !TeleportZonePreparation.BurstEnabled) return;
            try
            {
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                VerifyNativeContracts();
                native = (Func<ZoneSystem, Vector3, bool>)Delegate.CreateDelegate(typeof(Func<ZoneSystem, Vector3, bool>), Create);
                firstSpawn = AccessTools.FieldRefAccess<Game, bool>("m_firstSpawn");
                Patches.Patch(Update, transpiler: new HarmonyMethod(typeof(InitialLoadingOptimization), nameof(Transpile)));
                Installed = true; Status = "installed";
                logger.LogInfo("Initial loading acceleration installed (client initial spawn: " + enabled.Value + "; distant teleports: " + TeleportZonePreparation.BurstEnabled + ").");
            }
            catch (Exception error)
            {
                Patches.UnpatchSelf(); Installed = false; Status = "unsupported_native_layout";
                // The computed fingerprints are the one thing a log needs to explain a refusal.
                string computed;
                try { computed = "create=" + IlFingerprint.Compute(Create) + " poke=" + IlFingerprint.Compute(Poke); }
                catch (Exception inner) { computed = "fingerprint unavailable: " + inner.GetType().Name; }
                logger.LogWarning("Initial loading acceleration unavailable: " + error.GetType().Name + ": " + error.Message + " (" + computed + ")");
            }
        }

        private static void VerifyNativeContracts()
        {
            // Exact inspected IL: reject changed success/registration semantics after updates.
            VerifyHash(Create, "9E86A318CF06F1B4ADB27246D4A99EF5782F0B82F40FF54F6AB2BA1576C1F28C");
            VerifyHash(Poke, "5C6F82835A71EA1BB30DCBE5B1C32454053607D9841BC1686BD4314B7024D313");
            if (Create.IsStatic || Create.ReturnType != typeof(bool) || Poke.IsStatic || Poke.ReturnType != typeof(bool) ||
                Update.IsStatic || Update.ReturnType != typeof(void)) throw new InvalidOperationException("Native signature changed.");
        }

        // Token-independent IL fingerprint (see IlFingerprint): a game update that only
        // renumbers metadata no longer disables the acceleration, while any change to the
        // opcodes, constants, branches or referenced members still does.
        private static void VerifyHash(MethodInfo method, params string[] expected)
        {
            if (method == null || !expected.Contains(IlFingerprint.Compute(method)))
                throw new InvalidOperationException("Native zone contract changed.");
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var matches = code.Where(i => i.Calls(Create)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Unknown local-zone update layout.");
            // Keep the original instruction object, labels, exception blocks and stack contract.
            matches[0].opcode = OpCodes.Call;
            matches[0].operand = AccessTools.DeclaredMethod(typeof(InitialLoadingOptimization), nameof(CreateLocalZoneBurst));
            return code;
        }

        private static bool ConflictingPatches()
        {
            foreach (var method in GuardedMethods)
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (patch.owner == Patches.Id && method == Update && patch.PatchMethod.Name == nameof(Transpile)) continue;
                    // Only the known observational timer is allowed alongside the native contract.
                    if (patch.owner == Plugin.PluginId && patch.PatchMethod.DeclaringType == typeof(TimingHooks) &&
                        (patch.PatchMethod.Name == "Prefix" || patch.PatchMethod.Name == "Finalizer")) continue;
                    return true;
                }
            }
            return false;
        }

        private static bool Eligible(ZoneSystem zones, Game game, ZNet network, Vector3 point)
        {
            return Enabled && Thread.CurrentThread.ManagedThreadId == ownerThread &&
                game != null && network != null && ReferenceEquals(Game.instance, game) &&
                ReferenceEquals(ZNet.instance, network) && ReferenceEquals(ZoneSystem.instance, zones) &&
                !network.IsServer() && firstSpawn(game) && game.WaitingForRespawn() &&
                point.Equals(network.GetReferencePosition());
        }

        private static bool CreateLocalZoneBurst(ZoneSystem zones, Vector3 point)
        {
            // Disabled, nested and ordinary gameplay calls have no clocks or patch scans.
            if ((!Enabled && !TeleportEnabled) || inCall || Thread.CurrentThread.ManagedThreadId != ownerThread) return native(zones, point);
            if (TeleportEnabled && TeleportScreen(zones, point)) return TeleportBurst(zones, point);
            if (!Enabled) return native(zones, point);
            var game = Game.instance;
            var network = ZNet.instance;
            bool eligible;
            long started = 0;
            try
            {
                eligible = Eligible(zones, game, network, point);
                if (eligible) started = Stopwatch.GetTimestamp();
                if (eligible && ConflictingPatches()) { Status = "native_due_to_patch_conflict"; eligible = false; }
                if (eligible && !ReferenceEquals(episodeGame?.Target, game))
                {
                    episodeGame = new WeakReference(game); sequence++;
                    calls = firstSuccesses = extraCalls = extraSuccesses = noProgress = budgetStops = capStops = contextStops = failures = 0;
                    totalMs = maxMs = extraMs = extraMaxMs = 0;
                    startedUtc = DateTime.UtcNow.ToString("O");
                }
            }
            catch { Status = "native_due_to_guard_failure"; eligible = false; }
            if (!eligible) return native(zones, point);
            inCall = true;
            calls++;
            try
            {
                bool created = native(zones, point); // Always perform the original call, once.
                if (!created) { noProgress++; return false; }
                firstSuccesses++;
                for (int pass = 1; pass < 4; pass++)
                {
                    if (MillisecondsSince(started) >= 8) { budgetStops++; return true; }
                    bool continuing;
                    try { continuing = Eligible(zones, game, network, point) && !ConflictingPatches(); }
                    catch { continuing = false; Status = "native_due_to_guard_failure"; }
                    if (!continuing) { contextStops++; return true; }
                    extraCalls++;
                    long extraStarted = Stopwatch.GetTimestamp();
                    bool more;
                    try { more = native(zones, point); }
                    finally { double elapsed = MillisecondsSince(extraStarted); extraMs += elapsed; extraMaxMs = Math.Max(extraMaxMs, elapsed); }
                    if (!more) { noProgress++; return true; }
                    extraSuccesses++;
                }
                capStops++;
                return true; // A later false must not erase the mandatory call's success.
            }
            catch { failures++; throw; } // Native errors retain their original propagation.
            finally
            {
                inCall = false;
                double elapsed = MillisecondsSince(started); totalMs += elapsed; maxMs = Math.Max(maxMs, elapsed);
            }
        }

        private static bool TeleportScreen(ZoneSystem zones, Vector3 point)
        {
            try
            {
                ZNet network = ZNet.instance;
                return network != null && ReferenceEquals(ZoneSystem.instance, zones) && !network.IsServer() &&
                    point.Equals(network.GetReferencePosition()) && TeleportZonePreparation.MovedDistant(point) && !ConflictingPatches();
            }
            catch { Status = "native_due_to_guard_failure"; return false; }
        }

        // Same shape as the join burst: the native call once, then more passes while each creates a
        // zone, under a larger allowance since the loading screen hides these frames.
        private static bool TeleportBurst(ZoneSystem zones, Vector3 point)
        {
            long started = Stopwatch.GetTimestamp();
            inCall = true;
            teleportCalls++;
            try
            {
                if (!native(zones, point)) { teleportNoProgress++; return false; }
                for (int pass = 1; pass < TeleportMaxPasses; pass++)
                {
                    if (MillisecondsSince(started) >= TeleportZonePreparation.BurstMilliseconds) { teleportBudgetStops++; return true; }
                    if (!TeleportZonePreparation.MovedDistant(point)) return true;
                    if (!native(zones, point)) { teleportNoProgress++; return true; }
                    teleportExtraSuccesses++;
                }
                teleportCapStops++;
                return true;
            }
            catch { failures++; throw; }
            finally
            {
                inCall = false;
                teleportMaxMs = Math.Max(teleportMaxMs, MillisecondsSince(started));
            }
        }

        private static double MillisecondsSince(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("initial_loading_status", Status));
            labels.Add(new TextValue("initial_loading_enabled", Enabled ? "true" : "false"));
            if (TeleportEnabled)
            {
                gauges.Add(new NumberValue("teleport_zone_burst_calls", teleportCalls, "calls"));
                gauges.Add(new NumberValue("teleport_zone_burst_extra_zones", teleportExtraSuccesses, "zones"));
                gauges.Add(new NumberValue("teleport_zone_burst_budget_stops", teleportBudgetStops, "calls"));
                gauges.Add(new NumberValue("teleport_zone_burst_no_progress", teleportNoProgress, "calls"));
                gauges.Add(new NumberValue("teleport_zone_burst_cap_stops", teleportCapStops, "calls"));
                gauges.Add(new NumberValue("teleport_zone_burst_max_ms", Math.Round(teleportMaxMs, 1), "ms"));
                teleportCalls = teleportExtraSuccesses = teleportBudgetStops = teleportNoProgress = teleportCapStops = 0;
                teleportMaxMs = 0;
            }
            labels.Add(new TextValue("initial_loading_semantics", "latest_initial_join_cumulative; extra_successes_are_zone_registrations_not_objects_or_time_saved; inclusive_elapsed_not_CPU; soft_8ms_total_max_4_passes"));
            if (sequence == 0) return;
            labels.Add(new TextValue("initial_loading_started_utc", startedUtc));
            gauges.Add(new NumberValue("initial_loading_sequence", sequence, "sequence"));
            gauges.Add(new NumberValue("initial_loading_calls", calls, "calls"));
            gauges.Add(new NumberValue("initial_loading_first_successes", firstSuccesses, "zones"));
            gauges.Add(new NumberValue("initial_loading_extra_calls", extraCalls, "calls"));
            gauges.Add(new NumberValue("initial_loading_extra_successes", extraSuccesses, "zones"));
            gauges.Add(new NumberValue("initial_loading_no_progress_stops", noProgress, "calls"));
            gauges.Add(new NumberValue("initial_loading_budget_stops", budgetStops, "calls"));
            gauges.Add(new NumberValue("initial_loading_cap_stops", capStops, "calls"));
            gauges.Add(new NumberValue("initial_loading_context_stops", contextStops, "calls"));
            gauges.Add(new NumberValue("initial_loading_failures", failures, "calls"));
            gauges.Add(new NumberValue("initial_loading_total_ms", totalMs, "ms"));
            gauges.Add(new NumberValue("initial_loading_max_ms", maxMs, "ms"));
            gauges.Add(new NumberValue("initial_loading_extra_total_ms", extraMs, "ms"));
            gauges.Add(new NumberValue("initial_loading_extra_max_ms", extraMaxMs, "ms"));
        }

        internal static void Uninstall() { Patches.UnpatchSelf(); Installed = false; episodeGame = null; }
    }
}
