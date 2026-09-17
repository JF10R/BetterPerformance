using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Read-only sampled observation of the delay between a mined chunk disappearing and
    // its loot becoming visible on this client. Three timestamps on one process's clock:
    // the hit area is observed destroyed (t0), the item ZDO first arrives from the network
    // (t1), the item GameObject is created locally (t2). Cross-process Stopwatch origins
    // are offset on this runtime, so no server clock enters any of these durations.
    // Attribution is by position and time, never by identity: the game does not link a
    // destroyed hit area to the items it dropped.
    internal static class LootVisibilityTelemetry
    {
        private const int DestructionCapacity = 32, ArrivalCapacity = 256, PrefabCapacity = 1024, FailureLimit = 8;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".LootVisibilityTelemetry");
        private static readonly Dictionary<int, bool> PrefabKinds = new Dictionary<int, bool>(PrefabCapacity);
        private static LootVisibilityTracker<ZDOID>? tracker;
        private static ConfigEntry<bool>? enabled;
        private static ConfigEntry<float>? radius;
        private static ConfigEntry<int>? windowSeconds;
        private static ManualLogSource logger = null!;
        // One bounded session reference permits cheap identity checks. Reset releases it;
        // no game object or world is retained by CaptureSession.
        private static CaptureSession? capture;
        private static WeakReference<ZNetScene>? scene;
        private static double installedRadius, installedWindowMs;
        private static long unknownPrefabs, intervalFailures, failures;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource log)
        {
            logger = log;
            enabled = config.Bind("Diagnostics", "LootVisibilityEnabled", true,
                "Read-only sampled observation of the delay between a mined chunk disappearing and its loot becoming visible locally; attribution is by position and time, not identity. Client only.");
            radius = config.Bind("Diagnostics", "LootVisibilityRadius", 12f,
                new ConfigDescription("Attribution radius in metres from the destroyed rock root. A drop farther than this is counted as unattributed rather than assigned to the wrong chunk.", new AcceptableValueRange<float>(2f, 32f)));
            windowSeconds = config.Bind("Diagnostics", "LootVisibilityWindowSeconds", 5,
                new ConfigDescription("How long a destroyed hit area stays eligible for attribution. Loot arriving later is counted as unattributed, never as a long delay.", new AcceptableValueRange<int>(1, 15)));
            if (!enabled.Value) { Status = "disabled"; return; }
            if (Dedicated()) { Status = "dedicated-server"; return; }
            try
            {
                var (areaHealth, createZdo, createObject) = ValidateContracts();
                installedRadius = Math.Max(2, Math.Min(32, radius.Value));
                installedWindowMs = Math.Max(1, Math.Min(15, windowSeconds.Value)) * 1000.0;
                tracker = new LootVisibilityTracker<ZDOID>(DestructionCapacity, ArrivalCapacity,
                    installedRadius, installedWindowMs);
                Patches.Patch(areaHealth, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterSetAreaHealth)));
                Patches.Patch(createZdo, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterCreateNewZDO)));
                Patches.Patch(createObject, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterCreateObject)));
                Installed = Enabled = true;
                Status = "installed";
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                tracker = null;
                try { Patches.UnpatchSelf(); } catch { failures++; }
                logger.LogWarning("Loot visibility diagnostics unavailable; native behaviour retained: " +
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        // ZNet does not exist yet during plugin Awake, so this is also re-tested from the
        // hooks once a world exists; a dedicated server then unpatches instead of sampling.
        private static bool Dedicated() => !ReferenceEquals(ZNet.instance, null) && ZNet.instance.IsDedicated();

        private static (MethodInfo AreaHealth, MethodInfo CreateZdo, MethodInfo CreateObject) ValidateContracts()
        {
            var areaHealth = AccessTools.DeclaredMethod(typeof(MineRock5), "RPC_SetAreaHealth",
                new[] { typeof(long), typeof(int), typeof(float) })
                ?? throw new InvalidOperationException("MineRock5.RPC_SetAreaHealth(long, int, float) is missing.");
            if (areaHealth.IsStatic || areaHealth.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported MineRock5.RPC_SetAreaHealth signature.");
            // The private three-argument overload is the single creation point for a ZDO
            // the client has never seen; RPC_ZDOData reaches it leaving the prefab hash 0,
            // while ZNetView's local creation passes a non-zero hash.
            var createZdo = AccessTools.DeclaredMethod(typeof(ZDOMan), "CreateNewZDO",
                new[] { typeof(ZDOID), typeof(Vector3), typeof(int) })
                ?? throw new InvalidOperationException("ZDOMan.CreateNewZDO(ZDOID, Vector3, int) is missing.");
            if (createZdo.IsStatic || createZdo.ReturnType != typeof(ZDO))
                throw new InvalidOperationException("Unsupported ZDOMan.CreateNewZDO signature.");
            var createObject = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) })
                ?? throw new InvalidOperationException("ZNetScene.CreateObject(ZDO) is missing.");
            if (createObject.IsStatic || createObject.ReturnType != typeof(GameObject))
                throw new InvalidOperationException("Unsupported ZNetScene.CreateObject signature.");
            if (AccessTools.DeclaredMethod(typeof(ZDO), "GetPosition", Type.EmptyTypes)?.ReturnType != typeof(Vector3) ||
                AccessTools.DeclaredMethod(typeof(ZDO), "IsOwner", Type.EmptyTypes)?.ReturnType != typeof(bool) ||
                AccessTools.DeclaredMethod(typeof(ZDO), "GetPrefab", Type.EmptyTypes)?.ReturnType != typeof(int))
                throw new InvalidOperationException("Unsupported ZDO accessor contract.");
            return (areaHealth, createZdo, createObject);
        }

        private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        // Returns the live tracker only while a capture is running on a non-dedicated
        // process. Every hook leaves here without a clock read or an allocation.
        private static LootVisibilityTracker<ZDOID>? Observing()
        {
            if (!Enabled || tracker == null || enabled == null || !enabled.Value) return null;
            if (System.Threading.Volatile.Read(ref TimingHooks.Current) == null) return null;
            return tracker;
        }

        private static bool Bind(LootVisibilityTracker<ZDOID> current)
        {
            var session = System.Threading.Volatile.Read(ref TimingHooks.Current);
            if (session == null) return false;
            if (!ReferenceEquals(capture, session)) { Reset(); capture = session; }
            // Unpatching from inside a running patch is unsafe; stop observing instead and
            // let the plugin's own shutdown remove the patches.
            if (Dedicated()) { Enabled = false; Status = "dedicated-server"; current.Clear(false); return false; }
            var currentScene = ZNetScene.instance;
            if (currentScene == null) return false;
            if (scene == null || !scene.TryGetTarget(out var previous) || !ReferenceEquals(previous, currentScene))
            {
                current.Clear(true);
                PrefabKinds.Clear();
                scene = new WeakReference<ZNetScene>(currentScene);
            }
            return true;
        }

        private static void AfterSetAreaHealth(MineRock5 __instance, float __2)
        {
            var current = Observing();
            if (current == null || !(__2 <= 0) || __instance == null) return;
            try
            {
                if (!Bind(current)) return;
                // The rock root, not the hit-area centre: the attribution radius absorbs
                // the offset between them, which is why the radius cannot be small.
                var position = __instance.transform.position;
                current.Destroyed(position.x, position.y, position.z, NowMs());
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void AfterCreateNewZDO(ZDOID __0, Vector3 __1, int __2)
        {
            var current = Observing();
            // A non-zero prefab hash means this process created the ZDO itself; only the
            // RPC_ZDOData path leaves it zero. No pending destruction means no work at all.
            if (current == null || __2 != 0 || current.PendingDestructions == 0) return;
            try
            {
                if (!Bind(current)) return;
                // The prefab is not deserialized yet, so the object cannot be classified
                // here. Position and time are the only evidence available at arrival.
                current.Arrived(__0, __1.x, __1.y, __1.z, NowMs());
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void AfterCreateObject(ZDO __0, GameObject? __result)
        {
            var current = Observing();
            if (current == null || __result == null || current.PendingDestructions == 0) return;
            try
            {
                if (!Bind(current)) return;
                int hash = __0.GetPrefab();
                if (!PrefabKinds.TryGetValue(hash, out bool loot))
                {
                    if (PrefabKinds.Count >= PrefabCapacity) { unknownPrefabs++; return; }
                    var prefab = ZNetScene.instance.GetPrefab(hash);
                    if (prefab == null) { unknownPrefabs++; return; }
                    loot = prefab.GetComponent<ItemDrop>() != null;
                    PrefabKinds.Add(hash, loot);
                }
                if (!loot) return;
                var position = __0.GetPosition();
                current.Created(__0.m_uid, position.x, position.y, position.z, NowMs(), __0.IsOwner());
            }
            catch (Exception exception) { Fail(exception); }
        }

        // A single failure is tolerated and counted; repeated failures stop the module for
        // the process rather than spending main-thread time on a broken observation path.
        private static void Fail(Exception exception)
        {
            failures++;
            intervalFailures++;
            if (failures < FailureLimit) return;
            Enabled = false;
            Status = "failed";
            tracker?.Clear(true);
            PrefabKinds.Clear();
            logger.LogWarning("Loot visibility diagnostics disabled after repeated observation failures: " +
                exception.GetType().Name);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            // A dedicated server reaches Bind only once a destruction is pending, which
            // never happens there, so without this the label would read "installed" for a
            // process that will never observe anything. Sampling runs with a world present.
            if (Enabled && Dedicated()) { Enabled = false; Status = "dedicated-server"; tracker?.Clear(false); }
            labels.Add(new TextValue("loot_visibility_status", Status));
            labels.Add(new TextValue("loot_visibility_enabled", Enabled && Status == "installed" ? "true" : "false"));
            gauges.Add(new NumberValue("loot_visibility_probe_failures", intervalFailures, "calls"));
            gauges.Add(new NumberValue("loot_visibility_unknown_prefabs", unknownPrefabs, "observations"));
            intervalFailures = unknownPrefabs = 0;
            if (tracker == null) return;
            var summary = tracker.Drain(NowMs());
            gauges.Add(new NumberValue("loot_visibility_radius", installedRadius, "metres"));
            gauges.Add(new NumberValue("loot_visibility_window", installedWindowMs / 1000.0, "seconds"));
            gauges.Add(new NumberValue("loot_visibility_network_count", summary.NetworkCount, "observations"));
            gauges.Add(new NumberValue("loot_visibility_network_sum", summary.NetworkSumMs, "ms"));
            gauges.Add(new NumberValue("loot_visibility_network_max", summary.NetworkMaxMs, "ms"));
            gauges.Add(new NumberValue("loot_visibility_creation_count", summary.CreationCount, "observations"));
            gauges.Add(new NumberValue("loot_visibility_creation_sum", summary.CreationSumMs, "ms"));
            gauges.Add(new NumberValue("loot_visibility_creation_max", summary.CreationMaxMs, "ms"));
            gauges.Add(new NumberValue("loot_visibility_perceived_count", summary.PerceivedCount, "observations"));
            gauges.Add(new NumberValue("loot_visibility_perceived_sum", summary.PerceivedSumMs, "ms"));
            gauges.Add(new NumberValue("loot_visibility_perceived_max", summary.PerceivedMaxMs, "ms"));
            var bounds = LootVisibilityTracker<ZDOID>.BucketUpperBoundsMs;
            for (int i = 0; i < LootVisibilityTracker<ZDOID>.BucketCount; i++)
                gauges.Add(new NumberValue("loot_visibility_perceived_bucket_" +
                    (i < bounds.Length ? bounds[i].ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "over"),
                    summary.PerceivedBuckets[i], "observations"));
            gauges.Add(new NumberValue("loot_visibility_unattributed", summary.Unattributed, "observations"));
            gauges.Add(new NumberValue("loot_visibility_arrival_missing", summary.ArrivalMissing, "observations"));
            gauges.Add(new NumberValue("loot_visibility_locally_owned", summary.LocallyOwned, "observations"));
            gauges.Add(new NumberValue("loot_visibility_censored", summary.Censored, "entries"));
            gauges.Add(new NumberValue("loot_visibility_destruction_overflow", summary.Overflowed, "destructions"));
            gauges.Add(new NumberValue("loot_visibility_arrival_capacity_skips", summary.ArrivalCapacitySkipped, "observations"));
            gauges.Add(new NumberValue("loot_visibility_non_monotonic", summary.NonMonotonic, "durations"));
            gauges.Add(new NumberValue("loot_visibility_arrivals_expired", summary.ArrivalsExpired, "arrivals"));
            gauges.Add(new NumberValue("loot_visibility_pending_destructions", summary.PendingDestructions, "destructions"));
            gauges.Add(new NumberValue("loot_visibility_pending_arrivals", summary.PendingArrivals, "arrivals"));
        }

        internal static void Reset()
        {
            tracker?.Clear(false);
            PrefabKinds.Clear();
            capture = null;
            scene = null;
            unknownPrefabs = intervalFailures = 0;
        }

        internal static void Uninstall()
        {
            Reset();
            failures = 0;
            installedRadius = installedWindowMs = 0;
            try { Patches.UnpatchSelf(); } catch { }
            tracker = null;
            Installed = Enabled = false;
            Status = "disabled";
            enabled = null;
            radius = null;
            windowSeconds = null;
        }
    }
}
