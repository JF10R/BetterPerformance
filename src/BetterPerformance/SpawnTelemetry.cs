using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using BetterPerformance.Core;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Per-prefab spawn outcomes observed at native decision points of SpawnSystem.UpdateSpawnList.
    // Observers only: no skip, no result change. Names are read once per prefab, never per call.
    internal static class SpawnTelemetry
    {
        private const int TrackedPrefabs = 32, MappedPrefabs = 128, ExportedRows = 12;
        private static readonly SpawnTally Tally = new SpawnTally(TrackedPrefabs);
        private static readonly Dictionary<GameObject, int> Slots = new Dictionary<GameObject, int>(64, new ReferenceComparer());
        private static CaptureSession? capture;
        private static ManualLogSource? log;
        private static int mainThread, listDepth, pointSlot = -1;
        private static bool pointInBase, tablePending;
        private static object? describedWorld;
        private static string? table;
        private static string listStatus = "disabled", speciesStatus = "disabled", capStatus = "disabled",
            pointStatus = "disabled", rejectStatus = "disabled", baseStatus = "disabled", crowdStatus = "disabled";

        private sealed class ReferenceComparer : IEqualityComparer<GameObject>
        {
            public bool Equals(GameObject? left, GameObject? right) => ReferenceEquals(left, right);
            public int GetHashCode(GameObject value) => RuntimeHelpers.GetHashCode(value);
        }

        // Order: UpdateSpawnList, Spawn, GetNrOfZDOInstances, FindBaseSpawnPoint, IsSpawnPointGood,
        // EffectArea.IsPointInsideArea, HaveInstanceInRange. Each entry resolves alone; null when drifted.
        internal static MethodInfo?[] Targets()
        {
            Type? data = AccessTools.Inner(typeof(SpawnSystem), "SpawnData");
            Type vector = typeof(Vector3);
            if (data == null) return new MethodInfo?[7];
            return new[]
            {
                Exact(() => typeof(SpawnSystem), "UpdateSpawnList", typeof(void),
                    () => new[] { typeof(List<>).MakeGenericType(data), typeof(DateTime), typeof(bool), typeof(string) }),
                Exact(() => typeof(SpawnSystem), "Spawn", typeof(void), () => new[] { data, vector, typeof(bool) }),
                Exact(() => typeof(SpawnSystem), "GetNrOfZDOInstances", typeof(int),
                    () => new[] { typeof(GameObject), typeof(List<ZDO>), typeof(bool) }),
                Exact(() => typeof(SpawnSystem), "FindBaseSpawnPoint", typeof(bool),
                    () => new[] { data, typeof(List<Player>), vector.MakeByRefType(), typeof(Player).MakeByRefType() }),
                Exact(() => typeof(SpawnSystem), "IsSpawnPointGood", typeof(bool), () => new[] { data, vector.MakeByRefType() }),
                Exact(() => typeof(EffectArea), "IsPointInsideArea", typeof(EffectArea),
                    () => new[] { vector, typeof(EffectArea.Type), typeof(float) }),
                Exact(() => typeof(SpawnSystem), "HaveInstanceInRange", typeof(bool), () => new[] { typeof(GameObject), vector, typeof(float) })
            };
        }

        private static MethodInfo? Exact(Func<Type> owner, string name, Type result, Func<Type[]> arguments)
        {
            try
            {
                MethodInfo? method = AccessTools.DeclaredMethod(owner(), name, arguments());
                return method != null && method.ReturnType == result ? method : null;
            }
            catch (Exception exception)
            {
                log?.LogWarning("Spawn probe " + name + " unresolved: " + exception.GetType().Name);
                return null;
            }
        }

        internal static void Install(Harmony harmony, ManualLogSource logger, bool enabled)
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            log = logger;
            if (enabled)
            {
                MethodInfo?[] targets;
                try { targets = Targets(); }
                catch (Exception exception)
                { targets = new MethodInfo?[7]; logger.LogWarning("Spawn probes unavailable: " + exception.GetType().Name); }
                listStatus = Patch(harmony, logger, targets[0], nameof(ListPrefix), null, nameof(ListFinalizer));
                speciesStatus = Patch(harmony, logger, targets[1], nameof(SpawnPrefix), null, null);
                pointStatus = Patch(harmony, logger, targets[3], nameof(SearchPrefix), nameof(SearchPostfix), null);
                // Cap refusals are derived against point searches; both hooks or neither.
                capStatus = listStatus != "enabled" || pointStatus != "enabled" ? "unavailable"
                    : Patch(harmony, logger, targets[2], null, nameof(CapPostfix), null);
                rejectStatus = Patch(harmony, logger, targets[4], nameof(PointPrefix), null, nameof(PointFinalizer));
                baseStatus = rejectStatus != "enabled" ? "unavailable"
                    : Patch(harmony, logger, targets[5], null, nameof(BasePostfix), null);
                crowdStatus = listStatus != "enabled" ? "unavailable"
                    : Patch(harmony, logger, targets[6], null, nameof(CrowdPostfix), null);
            }
            TimingHooks.Availability.Add(new TextValue("probe.SpawnSpecies", speciesStatus));
            TimingHooks.Availability.Add(new TextValue("probe.SpawnRefusals", capStatus + "/" + pointStatus + "/" + rejectStatus + "/" + baseStatus + "/" + crowdStatus));
        }

        private static string Patch(Harmony harmony, ManualLogSource logger, MethodInfo? method, string? prefix, string? postfix, string? finalizer)
        {
            if (method == null) return "unavailable";
            try
            {
                harmony.Patch(method,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(SpawnTelemetry), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(SpawnTelemetry), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(SpawnTelemetry), finalizer));
                return "enabled";
            }
            catch (Exception exception)
            {
                logger.LogWarning("Spawn probe " + method.Name + " unavailable: " + exception.GetType().Name);
                return "patch_failed";
            }
        }

        private static CaptureSession? Active()
        {
            var session = Volatile.Read(ref TimingHooks.Current);
            if (session == null) return null;
            if (Thread.CurrentThread.ManagedThreadId != mainThread)
            { session.RecordProbeFailure(); return null; }
            if (!ReferenceEquals(capture, session))
            {
                Reset();
                capture = session;
            }
            return session;
        }

        private static int Slot(GameObject? prefab)
        {
            if (prefab is null) return SpawnTally.OtherSlot;
            if (Slots.TryGetValue(prefab!, out int slot)) return slot;
            if (Slots.Count >= MappedPrefabs) return SpawnTally.OtherSlot;
            slot = Tally.Register(prefab!.name);
            Slots[prefab] = slot;
            return slot;
        }

        private static void ListPrefix(SpawnSystem __instance)
        {
            listDepth++;
            try
            {
                object? world = ZNet.instance;
                if (ReferenceEquals(world, describedWorld) || world == null) return;
                describedWorld = world;
                table = Describe(__instance, Heightmap.Biome.Mountain);
                tablePending = true;
                log?.LogInfo("Spawn table (Mountain, runtime data): " + table);
            }
            catch { capture?.RecordProbeFailure(); }
        }

        private static void ListFinalizer() => listDepth--;

        private static void SpawnPrefix(SpawnSystem.SpawnData __0, bool __2)
        {
            CaptureSession? session = null;
            try
            {
                session = Active();
                if (session == null || SpawnSystem.m_nospawn || __0 == null) return;
                Tally.Add(Slot(__0.m_prefab), __2 ? SpawnCounter.Event
                    : EnvMan.IsNight() ? SpawnCounter.NaturalNight : SpawnCounter.NaturalDay);
            }
            catch { session?.RecordProbeFailure(); }
        }

        private static void CapPostfix(GameObject __0)
        {
            if (listDepth <= 0) return;
            CaptureSession? session = null;
            try
            {
                session = Active();
                if (session != null) Tally.Add(Slot(__0), SpawnCounter.CapChecks);
            }
            catch { session?.RecordProbeFailure(); }
        }

        private static void SearchPrefix(SpawnSystem.SpawnData __0, out int __state)
        {
            __state = -1;
            CaptureSession? session = null;
            try
            {
                session = Active();
                if (session == null || __0 == null) return;
                int slot = Slot(__0.m_prefab);
                Tally.Add(slot, SpawnCounter.PointSearches);
                if (__0.m_maxSpawned > 0) Tally.Add(slot, SpawnCounter.CappedPointSearches);
                __state = slot;
            }
            catch { session?.RecordProbeFailure(); }
        }

        private static void SearchPostfix(bool __result, int __state)
        {
            if (__state >= 0 && !__result) Tally.Add(__state, SpawnCounter.PointSearchFailed);
        }

        private static void PointPrefix(SpawnSystem.SpawnData __0, out int __state)
        {
            __state = -1;
            CaptureSession? session = null;
            try
            {
                session = Active();
                if (session == null || __0 == null) return;
                __state = Slot(__0.m_prefab);
                pointInBase = false;
                pointSlot = __state;
            }
            catch { session?.RecordProbeFailure(); }
        }

        // Finalizer, not postfix: a native throw must still disarm the player-base observer.
        private static void PointFinalizer(bool __result, int __state, Exception? __exception)
        {
            if (__state < 0) return;
            pointSlot = -1;
            if (__exception == null && !__result)
            {
                Tally.Add(__state, SpawnCounter.PointsRejected);
                if (pointInBase) Tally.Add(__state, SpawnCounter.PointsInPlayerBase);
            }
            pointInBase = false;
        }

        // Runs for every caller of IsPointInsideArea; the armed-slot compare comes first.
        private static void BasePostfix(EffectArea.Type __1, EffectArea __result)
        {
            if (pointSlot < 0 || Thread.CurrentThread.ManagedThreadId != mainThread) return;
            if ((__1 & EffectArea.Type.PlayerBase) != 0 && !(__result is null)) pointInBase = true;
        }

        private static void CrowdPostfix(GameObject __0, bool __result)
        {
            if (!__result || listDepth <= 0) return;
            CaptureSession? session = null;
            try
            {
                session = Active();
                if (session != null) Tally.Add(Slot(__0), SpawnCounter.Crowded);
            }
            catch { session?.RecordProbeFailure(); }
        }

        private static string Describe(SpawnSystem system, Heightmap.Biome biome)
        {
            var text = new StringBuilder();
            int entries = 0;
            foreach (SpawnSystemList list in system.m_spawnLists)
            {
                if (list is null) continue;
                foreach (SpawnSystem.SpawnData d in list.m_spawners)
                {
                    if (d == null || (d.m_biome & biome) == 0) continue;
                    if (entries++ > 0) text.Append("; ");
                    string time = d.m_spawnAtDay ? (d.m_spawnAtNight ? "any" : "day") : (d.m_spawnAtNight ? "night" : "never");
                    text.Append(d.m_name).Append('/').Append(d.m_prefab is null ? "?" : d.m_prefab.name)
                        .Append(" on=").Append(d.m_enabled ? 1 : 0)
                        .Append(" time=").Append(time)
                        .Append(" env=").Append(d.m_requiredEnvironments.Count == 0 ? "-" : string.Join("|", d.m_requiredEnvironments.ToArray()))
                        .Append(" key=").Append(string.IsNullOrEmpty(d.m_requiredGlobalKey) ? "-" : d.m_requiredGlobalKey)
                        .Append(" event=").Append(string.IsNullOrEmpty(d.m_requiredPersistentEvent) ? "-" : d.m_requiredPersistentEvent)
                        .Append(" max=").Append(d.m_maxSpawned)
                        .Append(" every=").Append(Number(d.m_spawnInterval)).Append('s')
                        .Append(" chance=").Append(Number(d.m_spawnChance)).Append('%')
                        .Append(" group=").Append(d.m_groupSizeMin).Append('-').Append(d.m_groupSizeMax)
                        .Append(" in_base=").Append(d.m_insidePlayerBase ? 1 : 0)
                        .Append(" alt=").Append(Number(d.m_minAltitude)).Append("..").Append(Number(d.m_maxAltitude))
                        .Append(" radius=").Append(Number(d.m_spawnRadiusMin)).Append('-').Append(Number(d.m_spawnRadiusMax))
                        .Append(" min_gap=").Append(Number(d.m_spawnDistance));
                }
            }
            return entries == 0 ? "none" : entries.ToString(CultureInfo.InvariantCulture) + " entries: " + text;
        }

        // Separate method so a type-load failure stays inside the caller's try.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string? CurrentEnvironment() => EnvMan.instance?.GetCurrentEnvironment()?.m_name;

        private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("spawn_list_context_probe_status", listStatus));
            labels.Add(new TextValue("spawn_species_probe_status", speciesStatus));
            labels.Add(new TextValue("spawn_cap_probe_status", capStatus));
            labels.Add(new TextValue("spawn_point_search_probe_status", pointStatus));
            labels.Add(new TextValue("spawn_point_reject_probe_status", rejectStatus));
            labels.Add(new TextValue("spawn_player_base_probe_status", baseStatus));
            labels.Add(new TextValue("spawn_crowding_probe_status", crowdStatus));
            labels.Add(new TextValue("spawn_species_scope", "zone_owner_with_local_player; creatures_not_attempts; chance_interval_key_environment_time_refusals_unobserved"));
            try
            {
                string? environment = CurrentEnvironment();
                if (!string.IsNullOrEmpty(environment)) labels.Add(new TextValue("spawn_environment", environment!));
            }
            catch { capture?.RecordProbeFailure(); }
            if (tablePending && table != null)
            {
                labels.Add(new TextValue("spawn_table_mountain", table));
                tablePending = false;
            }
            SpawnRow[] rows = Tally.Drain(ExportedRows);
            var total = new SpawnRow();
            foreach (SpawnRow row in rows)
            {
                total.NaturalDay += row.NaturalDay; total.NaturalNight += row.NaturalNight; total.Event += row.Event;
                total.CapRefused += row.CapRefused; total.PointSearches += row.PointSearches;
                total.PointSearchFailed += row.PointSearchFailed; total.PointsRejected += row.PointsRejected;
                total.PointsInPlayerBase += row.PointsInPlayerBase; total.Crowded += row.Crowded;
            }
            Add(gauges, "spawn_", total, always: true);
            foreach (SpawnRow row in rows)
                Add(gauges, "spawn_by_prefab_" + SpawnTally.GaugeKey(row.Name) + "_", row, always: false);
        }

        private static void Add(List<NumberValue> gauges, string prefix, SpawnRow row, bool always)
        {
            void One(string name, long value, string unit)
            { if (always || value != 0) gauges.Add(new NumberValue(prefix + name, value, unit)); }
            One("creatures_day", row.NaturalDay, "creatures");
            One("creatures_night", row.NaturalNight, "creatures");
            One("creatures_event", row.Event, "creatures");
            One("cap_refused", row.CapRefused, "checks");
            One("point_searches", row.PointSearches, "searches");
            One("point_search_failed", row.PointSearchFailed, "searches");
            One("points_rejected", row.PointsRejected, "points");
            One("points_in_player_base_area", row.PointsInPlayerBase, "points");
            One("crowded_refused", row.Crowded, "checks");
        }

        internal static void Finish(List<NumberValue> gauges, List<TextValue> labels)
        {
            Sample(gauges, labels);
            Reset();
        }

        internal static void Reset()
        {
            Tally.Reset();
            Slots.Clear();
            capture = null;
            tablePending = table != null;
        }
    }
}
