using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Observation of the replication send path. Reads identifiers, positions and the
    // peer's own last-sync times; changes nothing and is independent of the optional
    // cadence switches. Its sync-list postfix runs first, so what it measures is the
    // vanilla selection even when the resend interval is active.
    internal static class ReplicationTelemetry
    {
        private const int PrefabCapacity = 32, ExportedPrefabs = 6, ListLimit = 65536;
        private static readonly double[] IntervalBounds = { 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2 };
        private static readonly double[] DistanceBounds = { 8, 16, 32, 64, 128, 256, 512 };
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".ReplicationTelemetry");
        private static readonly KeyedBucketHistograms Intervals = new KeyedBucketHistograms(PrefabCapacity, IntervalBounds);
        private static readonly BucketHistogram Distances = new BucketHistogram(DistanceBounds);
        private static readonly Dictionary<int, string> PrefabNames = new Dictionary<int, string>(PrefabCapacity);
        private static ConfigEntry<bool> configured = null!;
        private static AccessTools.FieldRef<ZDOMan, int>? zdosSent;
        private static List<ZDO>? syncList;
        private static int ownerThread;
        private static long passes, entries, forcedEntries, prioritizedEntries, firstSends;
        private static long sends, truncatedSends, truncatedEntries, boundedSkips, probeFailures, otherThreadSkips;

        internal static bool Enabled { get; set; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct SendScope { internal bool Active; internal int SentBefore; }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            configured = config.Bind("Diagnostics", "ReplicationCadenceTelemetryEnabled", true,
                "Export per-prefab resend intervals, send distances, forced sends and byte-budget truncations. Observation only; independent of the [Replication] switches.");
            if (!configured.Value) { Status = "disabled"; return; }
            try
            {
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                ZdoPeerAccess.Resolve();
                MethodInfo syncListMethod = AccessTools.DeclaredMethod(typeof(ZDOMan), "CreateSyncList",
                    new[] { ZdoPeerAccess.PeerType, typeof(List<ZDO>) })
                    ?? throw new InvalidOperationException("ZDOMan.CreateSyncList is missing.");
                MethodInfo sendMethod = AccessTools.DeclaredMethod(typeof(ZDOMan), "SendZDOs",
                    new[] { ZdoPeerAccess.PeerType, typeof(bool) })
                    ?? throw new InvalidOperationException("ZDOMan.SendZDOs is missing.");
                if (syncListMethod.ReturnType != typeof(void) || sendMethod.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unsupported replication signatures.");
                if (AccessTools.DeclaredField(typeof(ZDOMan), "m_zdosSent")?.FieldType != typeof(int))
                    throw new InvalidOperationException("ZDOMan.m_zdosSent is missing.");
                zdosSent = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosSent");
                // First, so the histogram describes the vanilla selection; the optional
                // resend interval postfix is registered last and removes entries after it.
                Patches.Patch(syncListMethod, postfix: new HarmonyMethod(typeof(ReplicationTelemetry), nameof(AfterCreateSyncList)) { priority = Priority.First });
                Installed = true;
                // The send counters are a separate patch: losing them must not cost the
                // per-prefab histograms, which are the measurement that decides the work.
                try
                {
                    Patches.Patch(sendMethod,
                        prefix: new HarmonyMethod(typeof(ReplicationTelemetry), nameof(BeforeSend)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(ReplicationTelemetry), nameof(AfterSend)) { priority = Priority.Last });
                    Status = "installed";
                }
                catch (Exception exception)
                {
                    Status = "installed_without_send_counters";
                    logger.LogWarning("Replication send counters unavailable; selection histograms retained: " + exception.GetType().Name);
                }
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                // A refused unpatch leaves inert hooks behind; it is reported, never
                // propagated into the game's startup path.
                try { Patches.UnpatchSelf(); } catch (Exception removal) { Status = "unpatch_failed:" + removal.GetType().Name; }
                logger.LogWarning("Replication cadence telemetry unavailable: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static bool Observe()
        {
            if (!Enabled || !Installed) return false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread) { otherThreadSkips++; return false; }
            return true;
        }

        private static void AfterCreateSyncList(object __0, List<ZDO> __1)
        {
            syncList = null;
            if (!Observe() || __1 == null) return;
            try
            {
                syncList = __1;
                if (__1.Count > ListLimit) { boundedSkips++; return; }
                var syncTimes = ZdoPeerAccess.SyncTimes;
                object? zdos = ZdoPeerAccess.Zdos(__0);
                HashSet<ZDOID>? forced = ZdoPeerAccess.ForceSend(__0);
                ZNetPeer? peer = ZdoPeerAccess.NetPeer(__0);
                if (syncTimes == null || zdos == null) return;
                bool hasReference = peer != null;
                Vector3 reference = hasReference ? peer!.GetRefPos() : Vector3.zero;
                float now = Time.time;
                passes++;
                foreach (ZDO zdo in __1)
                {
                    if (ReferenceEquals(zdo, null)) continue;
                    entries++;
                    if (zdo.Type == ZDO.ObjectType.Prioritized) prioritizedEntries++;
                    if (forced != null && forced.Contains(zdo.m_uid)) forcedEntries++;
                    if (syncTimes.TryGet(zdos, zdo.m_uid, out float syncTime)) Intervals.Add(zdo.GetPrefab(), now - syncTime);
                    else firstSends++;
                    if (hasReference) Distances.Add(Vector3.Distance(zdo.GetPosition(), reference));
                }
            }
            catch { probeFailures++; }
        }

        // The send loop is the only early exit in SendZDOs and it is the byte-budget
        // break, so a shortfall between the selected list and m_zdosSent counts exactly
        // the objects the budget displaced to a later cycle.
        private static void BeforeSend(ZDOMan __instance, out SendScope __state)
        {
            __state = default;
            syncList = null;
            try
            {
                if (!Observe() || zdosSent == null || __instance == null) return;
                __state = new SendScope { Active = true, SentBefore = zdosSent(__instance) };
            }
            catch { probeFailures++; __state = default; }
        }

        private static void AfterSend(ZDOMan __instance, SendScope __state)
        {
            if (!__state.Active) return;
            try
            {
                if (zdosSent == null || __instance == null || syncList == null) return;
                int selected = syncList.Count;
                int sent = zdosSent(__instance) - __state.SentBefore;
                syncList = null;
                if (selected <= 0 || sent < 0) return;
                sends++;
                if (sent >= selected) return;
                truncatedSends++;
                truncatedEntries += selected - sent;
            }
            catch { probeFailures++; }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("replication_telemetry_status", Status));
            labels.Add(new TextValue("replication_scope",
                "zdoman_send_path_main_thread; per_peer_selection_pass; vanilla_selection_before_optional_deferral; histograms_are_per_interval"));
            labels.Add(new TextValue("replication_resend_interval_bounds", BucketHistogram.Describe(IntervalBounds)));
            labels.Add(new TextValue("replication_send_distance_bounds", BucketHistogram.Describe(DistanceBounds)));
            gauges.Add(new NumberValue("replication_sync_list_passes", passes, "passes"));
            gauges.Add(new NumberValue("replication_sync_list_entries", entries, "objects"));
            gauges.Add(new NumberValue("replication_first_send_entries", firstSends, "objects"));
            gauges.Add(new NumberValue("replication_forced_send_entries", forcedEntries, "objects"));
            gauges.Add(new NumberValue("replication_prioritized_entries", prioritizedEntries, "objects"));
            gauges.Add(new NumberValue("replication_sends", sends, "calls"));
            gauges.Add(new NumberValue("replication_budget_truncated_sends", truncatedSends, "calls"));
            gauges.Add(new NumberValue("replication_budget_truncated_entries", truncatedEntries, "objects"));
            gauges.Add(new NumberValue("replication_tracked_prefabs", Intervals.TrackedKeys, "prefabs"));
            gauges.Add(new NumberValue("replication_prefab_capacity_dropped_samples", Intervals.DroppedSamples, "samples"));
            gauges.Add(new NumberValue("replication_telemetry_probe_failures", probeFailures, "calls"));
            gauges.Add(new NumberValue("replication_telemetry_other_thread_skips", otherThreadSkips, "calls"));
            gauges.Add(new NumberValue("replication_telemetry_bounded_skips", boundedSkips, "passes"));
            for (int bucket = 0; bucket < Distances.BucketCount; bucket++)
                gauges.Add(new NumberValue("replication_send_distance_b" + bucket.ToString(CultureInfo.InvariantCulture), Distances[bucket], "sends"));
            gauges.Add(new NumberValue("replication_send_distance_samples", Distances.Total, "sends"));
            var exported = new StringBuilder();
            foreach (KeyValuePair<int, BucketHistogram> row in Intervals.TopKeys(ExportedPrefabs))
            {
                string name = ResolvePrefab(row.Key);
                if (exported.Length > 0) exported.Append(',');
                exported.Append(name);
                for (int bucket = 0; bucket < row.Value.BucketCount; bucket++)
                    gauges.Add(new NumberValue("replication_resend_interval_" + name + "_b" + bucket.ToString(CultureInfo.InvariantCulture),
                        row.Value[bucket], "sends"));
            }
            labels.Add(new TextValue("replication_resend_interval_prefabs", exported.Length == 0 ? "none" : exported.ToString()));
            Reset();
        }

        // Names are resolved on the main thread at export, never inside a hook.
        private static string ResolvePrefab(int hash)
        {
            if (PrefabNames.TryGetValue(hash, out string cached)) return cached;
            string name = "prefab_" + hash.ToString(CultureInfo.InvariantCulture);
            try
            {
                ZNetScene scene = ZNetScene.instance;
                GameObject? prefab = scene == null ? null : scene.GetPrefab(hash);
                if (prefab != null && !string.IsNullOrEmpty(prefab.name)) name = Sanitize(prefab.name);
            }
            catch { probeFailures++; }
            if (PrefabNames.Count < PrefabCapacity) PrefabNames[hash] = name;
            return name;
        }

        private static string Sanitize(string name)
        {
            var text = new StringBuilder(name.Length);
            foreach (char character in name)
                text.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
            return text.ToString();
        }

        // Histograms and counters describe one export interval; totals across a capture
        // are recovered by summing records, as elsewhere in this plugin.
        internal static void Reset()
        {
            Intervals.Reset();
            Distances.Reset();
            passes = entries = forcedEntries = prioritizedEntries = firstSends = 0;
            sends = truncatedSends = truncatedEntries = boundedSkips = 0;
            probeFailures = otherThreadSkips = 0;
            syncList = null;
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Reset();
            PrefabNames.Clear();
            zdosSent = null;
            Installed = false;
            try { Patches.UnpatchSelf(); Status = "disabled"; }
            catch (Exception exception) { Status = "unpatch_failed:" + exception.GetType().Name; }
        }
    }
}
