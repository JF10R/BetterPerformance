using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Pair existing inclusive phase timings with their enclosing SpawnZone call.
    // No extra patches on hot vegetation/physics loops and no changes to generation.
    internal static class ZoneGenerationTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".ZoneGenerationTelemetry");
        private static readonly ZoneGenerationWindow Window = new ZoneGenerationWindow();
        private static readonly string[] Modes = { "client", "full", "ghost", "unknown" };
        private static int ownerThread, generation;
        private static long probeFailures, otherThreadSkips;
        [ThreadStatic] private static Scope current;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct Scope
        {
            internal CaptureSession? Session;
            internal int Generation;
            internal long Started;
            internal ZoneGenerationCall Call;
        }

        internal struct ScopeState
        {
            internal Scope Previous;
            internal int Generation;
            internal long Started;
            internal bool Entered;
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            if (!config.Bind("Diagnostics", "ZoneGenerationTelemetry", true,
                "Pair the slowest zone spawn per mode with its own existing inclusive phase timings. " +
                "Capture only; observes native generation without changing it. Requires restart.").Value) return;
            try
            {
                MethodInfo spawn = AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone",
                    new[] { typeof(Vector2s), typeof(ZoneSystem.SpawnMode), typeof(GameObject).MakeByRefType() })
                    ?? throw new InvalidOperationException("ZoneSystem.SpawnZone is unavailable.");
                if (spawn.IsStatic || spawn.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unsupported ZoneSystem.SpawnZone signature.");
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                Patches.Patch(spawn,
                    prefix: new HarmonyMethod(typeof(ZoneGenerationTelemetry), nameof(BeforeSpawn)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ZoneGenerationTelemetry), nameof(AfterSpawn)) { priority = Priority.Last });
                Installed = true;
                Status = "installed";
            }
            catch (Exception exception)
            {
                try { Patches.UnpatchSelf(); } catch { }
                Installed = false;
                Status = "unavailable";
                logger.LogWarning("Zone generation attribution unavailable: " + exception.GetType().Name);
            }
        }

        private static void BeforeSpawn(ZoneSystem.SpawnMode __1, out ScopeState __state)
        {
            __state = default;
            if (!Installed) return;
            CaptureSession? session = Volatile.Read(ref TimingHooks.Current);
            if (session == null) return;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            { Interlocked.Increment(ref otherThreadSkips); return; }
            try
            {
                long started = Stopwatch.GetTimestamp();
                __state = new ScopeState { Previous = current, Generation = generation, Started = started, Entered = true };
                current = new Scope { Session = session, Generation = generation, Started = started,
                    Call = new ZoneGenerationCall { Mode = Mode(__1) } };
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        // Called by TimingHooks only after it recorded the same native duration.
        // Almost all timed calls return at the first null check, without a clock read.
        internal static void RecordPhase(Metric metric, double elapsed, CaptureSession session)
        {
            if (current.Session == null) return;
            if (!ReferenceEquals(current.Session, session) || current.Generation != generation) return;
            switch (metric)
            {
                case Metric.HeightmapRegenerate: current.Call.HeightmapMs += elapsed; break;
                case Metric.ZonePlaceLocations: current.Call.LocationsMs += elapsed; break;
                case Metric.VegetationPlace: current.Call.VegetationMs += elapsed; break;
                case Metric.DungeonGenerate: current.Call.DungeonMs += elapsed; break;
            }
        }

        // Void finalizer preserves the native return value, out root, and exception.
        private static void AfterSpawn(ScopeState __state, bool __result, Exception? __exception)
        {
            if (!__state.Entered) return;
            try
            {
                Scope completed = current;
                if (completed.Generation != __state.Generation || completed.Started != __state.Started) return;
                current = __state.Previous;
                CaptureSession? session = Volatile.Read(ref TimingHooks.Current);
                if (current.Generation != generation || !ReferenceEquals(current.Session, session)) current = default;
                if (completed.Generation != generation || !ReferenceEquals(completed.Session, session)) return;
                completed.Call.ElapsedMs = (Stopwatch.GetTimestamp() - completed.Started) * 1000d / Stopwatch.Frequency;
                completed.Call.Succeeded = __result && __exception == null;
                completed.Call.Failed = __exception != null;
                if (!Window.Record(completed.Call)) Interlocked.Increment(ref probeFailures);
                // Preserve inclusive phase samples if a mod nests another SpawnZone.
                // An enclosing phase can overlap a nested phase of the same family;
                // these are sums of inclusive samples, never an exclusive partition.
                if (current.Session != null)
                {
                    current.Call.HeightmapMs += completed.Call.HeightmapMs;
                    current.Call.LocationsMs += completed.Call.LocationsMs;
                    current.Call.VegetationMs += completed.Call.VegetationMs;
                    current.Call.DungeonMs += completed.Call.DungeonMs;
                }
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static ZoneGenerationMode Mode(ZoneSystem.SpawnMode mode)
        {
            switch (mode)
            {
                case ZoneSystem.SpawnMode.Client: return ZoneGenerationMode.Client;
                case ZoneSystem.SpawnMode.Full: return ZoneGenerationMode.Full;
                case ZoneSystem.SpawnMode.Ghost: return ZoneGenerationMode.Ghost;
                default: return ZoneGenerationMode.Unknown;
            }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            string heightmapStatus = PhaseStatus(Metric.HeightmapRegenerate);
            string locationsStatus = PhaseStatus(Metric.ZonePlaceLocations);
            string vegetationStatus = PhaseStatus(Metric.VegetationPlace);
            string dungeonStatus = PhaseStatus(Metric.DungeonGenerate);
            ZoneGenerationSummary[] snapshot = Window.Drain();
            for (int i = 0; i < snapshot.Length; i++)
            {
                ZoneGenerationSummary summary = snapshot[i];
                string key = "zone_generation_" + Modes[i];
                gauges.Add(new NumberValue(key + "_calls", summary.Calls, "calls"));
                if (summary.Calls == 0) continue;
                gauges.Add(new NumberValue(key + "_succeeded", summary.Succeeded, "calls"));
                gauges.Add(new NumberValue(key + "_failed", summary.Failed, "calls"));
                gauges.Add(new NumberValue(key + "_over_50ms", summary.Over50Ms, "calls"));
                AddMs(gauges, key + "_sum_ms", summary.SumMs);
                AddMs(gauges, key + "_peak_ms", summary.Peak.ElapsedMs);
                if (heightmapStatus == "enabled") AddMs(gauges, key + "_peak_heightmap_ms", summary.Peak.HeightmapMs);
                if (locationsStatus == "enabled") AddMs(gauges, key + "_peak_locations_ms", summary.Peak.LocationsMs);
                if (vegetationStatus == "enabled") AddMs(gauges, key + "_peak_vegetation_ms", summary.Peak.VegetationMs);
                if (dungeonStatus == "enabled") AddMs(gauges, key + "_peak_dungeon_ms", summary.Peak.DungeonMs);
                labels.Add(new TextValue(key + "_peak_outcome", summary.Peak.Failed ? "exception" :
                    summary.Peak.Succeeded ? "returned_true" : "returned_false"));
            }
            gauges.Add(new NumberValue("zone_generation_probe_failures", Interlocked.Exchange(ref probeFailures, 0), "calls"));
            gauges.Add(new NumberValue("zone_generation_other_thread_skips", Interlocked.Exchange(ref otherThreadSkips, 0), "calls"));
            labels.Add(new TextValue("zone_generation_status", Status));
            labels.Add(new TextValue("zone_generation_heightmap_status", heightmapStatus));
            labels.Add(new TextValue("zone_generation_locations_status", locationsStatus));
            labels.Add(new TextValue("zone_generation_vegetation_status", vegetationStatus));
            labels.Add(new TextValue("zone_generation_dungeon_status", dungeonStatus));
            labels.Add(new TextValue("zone_generation_semantics",
                "interval_peak_per_mode; phases_of_same_call; inclusive_elapsed_overlap_do_not_sum; native_true_is_spawned_not_new_generation; phase_availability_uses_probe_labels"));
        }

        private static string PhaseStatus(Metric metric)
        {
            string name = "probe." + metric;
            foreach (TextValue probe in TimingHooks.Availability)
                if (probe.Name == name) return probe.Value;
            return "unavailable";
        }

        private static void AddMs(List<NumberValue> gauges, string name, double value) =>
            gauges.Add(new NumberValue(name, Math.Round(value, 3), "ms"));

        internal static void Reset()
        {
            generation++;
            current = default;
            Window.Reset();
            Interlocked.Exchange(ref probeFailures, 0);
            Interlocked.Exchange(ref otherThreadSkips, 0);
        }

        internal static void Uninstall()
        {
            Installed = false;
            Status = "disabled";
            Reset();
            try { Patches.UnpatchSelf(); }
            catch (Exception exception) { Status = "unpatch_failed_" + exception.GetType().Name; }
        }
    }
}
