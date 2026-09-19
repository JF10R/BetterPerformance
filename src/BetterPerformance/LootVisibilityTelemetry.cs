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
    // (t1), the item GameObject is registered in the scene (t2, ZNetScene.AddInstance, which
    // ZNetView.Awake calls for local instantiation and network creation alike, so a drop
    // this process makes itself is observed too). Cross-process Stopwatch origins
    // are offset on this runtime, so no server clock enters any of these durations.
    // Attribution is by position and time, never by identity: the game does not link a
    // destroyed hit area to the items it dropped.
    internal static class LootVisibilityTelemetry
    {
        private const int DestructionCapacity = 32, ArrivalCapacity = 256, PrefabCapacity = 1024, FailureLimit = 8;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".LootVisibilityTelemetry");
        private static readonly Dictionary<int, bool> PrefabKinds = new Dictionary<int, bool>(PrefabCapacity);
        // Prefab hash -> what kind of drop source its destruction is (0 none, 1 rock, 2 tree,
        // 3 log, 4 destructible with a drop table). Same bound and reset as PrefabKinds.
        private static readonly Dictionary<int, byte> SourceKinds = new Dictionary<int, byte>(PrefabCapacity);
        private static AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>>? instances;
        private static AccessTools.FieldRef<List<ItemDrop>>? itemDrops;
        private static long destroyedRock, destroyedTree, destroyedLog, destroyedDestructible;
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
                var (areaHealth, createZdo, addInstance, destroy, zdoDestroyed) = ValidateContracts();
                installedRadius = Math.Max(2, Math.Min(32, radius.Value));
                installedWindowMs = Math.Max(1, Math.Min(15, windowSeconds.Value)) * 1000.0;
                tracker = new LootVisibilityTracker<ZDOID>(DestructionCapacity, ArrivalCapacity,
                    installedRadius, installedWindowMs);
                Patches.Patch(areaHealth, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterSetAreaHealth)));
                Patches.Patch(createZdo, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterCreateNewZDO)));
                Patches.Patch(addInstance, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterAddInstance)));
                Patches.Patch(destroy, prefix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(BeforeDestroy)));
                Patches.Patch(zdoDestroyed, prefix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(BeforeZdoDestroyed)));
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

        private static (MethodInfo AreaHealth, MethodInfo CreateZdo, MethodInfo AddInstance, MethodInfo Destroy, MethodInfo ZdoDestroyed) ValidateContracts()
        {
            // t0 for trees, logs, plain rocks and destructibles with a drop table: the owner
            // destroys through ZNetScene.Destroy, every other client learns of it through
            // OnZDODestroyed. MineRock5 areas keep their own hook since the object survives.
            var destroy = AccessTools.DeclaredMethod(typeof(ZNetScene), "Destroy", new[] { typeof(GameObject) })
                ?? throw new InvalidOperationException("ZNetScene.Destroy(GameObject) is missing.");
            if (destroy.IsStatic || destroy.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported ZNetScene.Destroy signature.");
            var zdoDestroyed = AccessTools.DeclaredMethod(typeof(ZNetScene), "OnZDODestroyed", new[] { typeof(ZDO) })
                ?? throw new InvalidOperationException("ZNetScene.OnZDODestroyed(ZDO) is missing.");
            if (zdoDestroyed.IsStatic || zdoDestroyed.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported ZNetScene.OnZDODestroyed signature.");
            var instanceField = AccessTools.DeclaredField(typeof(ZNetScene), "m_instances");
            if (instanceField == null || instanceField.IsStatic || instanceField.FieldType != typeof(Dictionary<ZDO, ZNetView>))
                throw new InvalidOperationException("Unsupported ZNetScene.m_instances field.");
            instances = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>(instanceField);
            // Optional population gauge; its absence costs a gauge, not the probe.
            var dropList = AccessTools.DeclaredField(typeof(ItemDrop), "s_instances");
            itemDrops = dropList != null && dropList.IsStatic && dropList.FieldType == typeof(List<ItemDrop>)
                ? AccessTools.StaticFieldRefAccess<List<ItemDrop>>(dropList) : null;
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
            // t2. ZNetView.Awake ends with ZNetScene.AddInstance for both of its branches,
            // the adopted network ZDO and the one it creates itself, so a drop this process
            // instantiates as owner is observed here where ZNetScene.CreateObject never saw it.
            var addInstance = AccessTools.DeclaredMethod(typeof(ZNetScene), "AddInstance", new[] { typeof(ZDO), typeof(ZNetView) })
                ?? throw new InvalidOperationException("ZNetScene.AddInstance(ZDO, ZNetView) is missing.");
            if (addInstance.IsStatic || !addInstance.IsPublic || addInstance.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported ZNetScene.AddInstance signature.");
            // The instance answers the loot question directly, so no prefab lookup is needed.
            if (AccessTools.Property(typeof(ZNetView), "gameObject")?.PropertyType != typeof(GameObject) ||
                AccessTools.Method(typeof(ZNetView), "GetComponent", Type.EmptyTypes, new[] { typeof(ItemDrop) }) == null ||
                AccessTools.Method(typeof(ZNetView), "GetComponent", Type.EmptyTypes, new[] { typeof(TreeLog) }) == null)
                throw new InvalidOperationException("Unsupported ZNetView component accessor contract.");
            if (AccessTools.DeclaredMethod(typeof(ZDO), "GetPosition", Type.EmptyTypes)?.ReturnType != typeof(Vector3) ||
                AccessTools.DeclaredMethod(typeof(ZDO), "IsOwner", Type.EmptyTypes)?.ReturnType != typeof(bool) ||
                AccessTools.DeclaredMethod(typeof(ZDO), "GetPrefab", Type.EmptyTypes)?.ReturnType != typeof(int))
                throw new InvalidOperationException("Unsupported ZDO accessor contract.");
            return (areaHealth, createZdo, addInstance, destroy, zdoDestroyed);
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
                SourceKinds.Clear();
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
                // A destroyed MineRock5 area is a rock event; Record only sees the legacy
                // MineRock component, whose object is destroyed outright.
                destroyedRock++;
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

        // t2 for every drop this process shows, whichever peer created it: the owner's own
        // Instantiate reaches here through ZNetView.Awake exactly as a network creation does.
        private static void AfterAddInstance(ZDO __0, ZNetView __1)
        {
            var current = Observing();
            if (current == null || __0 == null || __1 == null || current.PendingDestructions == 0) return;
            try
            {
                if (!Bind(current)) return;
                int hash = __0.GetPrefab();
                if (!PrefabKinds.TryGetValue(hash, out bool loot))
                {
                    if (PrefabKinds.Count >= PrefabCapacity) { unknownPrefabs++; return; }
                    // A felled tree's visible result is its log, which is not an ItemDrop.
                    // The live instance carries both components, so no prefab lookup is needed.
                    loot = __1.GetComponent<ItemDrop>() != null || __1.GetComponent<TreeLog>() != null;
                    PrefabKinds.Add(hash, loot);
                }
                if (!loot) return;
                var position = __0.GetPosition();
                current.Created(__0.m_uid, position.x, position.y, position.z, NowMs(), __0.IsOwner());
            }
            catch (Exception exception) { Fail(exception); }
        }

        // Owner side: the object is destroyed locally and its drops are instantiated here.
        // Only an owned ZDO is a destruction; a non-owner reaching Destroy is a zone unload.
        private static void BeforeDestroy(GameObject __0)
        {
            var current = Observing();
            if (current == null || __0 == null) return;
            try
            {
                var view = __0.GetComponent<ZNetView>();
                var zdo = view != null ? view.GetZDO() : null;
                if (zdo == null || !zdo.IsOwner()) return;
                if (!Bind(current)) return;
                Record(current, zdo.GetPrefab(), __0, __0.transform.position);
            }
            catch (Exception exception) { Fail(exception); }
        }

        // Remote side: the owner's destruction arrives as a ZDO removal.
        private static void BeforeZdoDestroyed(ZNetScene __instance, ZDO __0)
        {
            var current = Observing();
            if (current == null || __0 == null || instances == null || __instance == null) return;
            try
            {
                if (!instances(__instance).TryGetValue(__0, out var view) || view == null) return;
                if (!Bind(current)) return;
                Record(current, __0.GetPrefab(), view.gameObject, __0.GetPosition());
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void Record(LootVisibilityTracker<ZDOID> current, int hash, GameObject go, Vector3 position)
        {
            if (!SourceKinds.TryGetValue(hash, out byte kind))
            {
                if (SourceKinds.Count >= PrefabCapacity) { unknownPrefabs++; return; }
                kind = go.GetComponent<TreeBase>() != null ? (byte)2
                    : go.GetComponent<TreeLog>() != null ? (byte)3
                    : go.GetComponent<MineRock>() != null ? (byte)1
                    : go.GetComponent<Destructible>() != null && go.GetComponent<DropOnDestroyed>() != null ? (byte)4
                    : (byte)0;
                SourceKinds.Add(hash, kind);
            }
            if (kind == 0) return;
            switch (kind)
            {
                case 1: destroyedRock++; break;
                case 2: destroyedTree++; break;
                case 3: destroyedLog++; break;
                default: destroyedDestructible++; break;
            }
            current.Destroyed(position.x, position.y, position.z, NowMs());
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
            SourceKinds.Clear();
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
            labels.Add(new TextValue("loot_visibility_scope",
                "t2_is_ZNetScene.AddInstance; arrival_missing_means_created_locally; rocks_are_MineRock5_areas"));
            gauges.Add(new NumberValue("loot_visibility_probe_failures", intervalFailures, "calls"));
            gauges.Add(new NumberValue("loot_visibility_unknown_prefabs", unknownPrefabs, "observations"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_rock", destroyedRock, "events"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_tree", destroyedTree, "events"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_log", destroyedLog, "events"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_destructible", destroyedDestructible, "events"));
            intervalFailures = unknownPrefabs = 0;
            destroyedRock = destroyedTree = destroyedLog = destroyedDestructible = 0;
            // Live dropped-item population: every one is a rigidbody the physics step pays for.
            try { if (itemDrops != null) gauges.Add(new NumberValue("item_drop_instances", itemDrops().Count, "instances")); }
            catch (Exception exception) { Fail(exception); }
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
            SourceKinds.Clear();
            destroyedRock = destroyedTree = destroyedLog = destroyedDestructible = 0;
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
