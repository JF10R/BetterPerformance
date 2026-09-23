using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
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
    // Attribution is by position, time and drop table, never by identity: the game does
    // not link a destroyed hit area to the items it dropped.
    // Two legs run on the other processes: the owner's Instantiate to its first send to
    // the server, and on the server a new drop's receipt to its first send to each peer.
    internal static class LootVisibilityTelemetry
    {
        private const int DestructionCapacity = 32, ArrivalCapacity = 256, PrefabCapacity = 1024, FailureLimit = 8;
        private const int LegCapacity = 64, FreshLimit = 256, NameCapacity = 64, NameLength = 40;
        // A drop settles after its spawn point: it falls and rolls before the owner's first send.
        private const double SettleMetres = 2, MinimumSpreadMetres = 4, MaxDropItems = 64;
        private const byte KindRock = 1, KindTree = 2, KindLog = 3, KindDestructible = 4, KindArea = 5, KindFracture = 6;
        private static readonly string[] KindNames = { "none", "rock", "tree", "log", "destructible", "area", "fracture" };
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".LootVisibilityTelemetry");
        private static readonly Dictionary<int, bool> PrefabKinds = new Dictionary<int, bool>(PrefabCapacity);
        // Prefab hash -> what its destruction can spawn; null for a prefab that is no drop
        // source. Same bound and reset as PrefabKinds.
        private static readonly Dictionary<int, SourceInfo?> Sources = new Dictionary<int, SourceInfo?>(PrefabCapacity);
        private static readonly Dictionary<int, string> PrefabNames = new Dictionary<int, string>(NameCapacity);
        private static readonly Func<int, int, bool> SpawnCheck = CanSpawn;
        private static readonly List<FreshZdo> Fresh = new List<FreshZdo>(FreshLimit);
        private static readonly PeerSentSet SentSet = new PeerSentSet();
        private static AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>>? instances;
        private static AccessTools.FieldRef<List<ItemDrop>>? itemDrops;
        // MineRock5's private hit-area list and the area collider; read by reflection, only
        // when an area is destroyed. Collider's own type lives in a module the plugin does
        // not reference, so bounds are read through its property.
        private static FieldInfo? hitAreasField, areaColliderField;
        private static PropertyInfo? colliderBounds, colliderEnabled;
        private static long destroyedRock, destroyedTree, destroyedLog, destroyedDestructible, destroyedFracture;
        private static long areaCentreFallbacks, emptyTables, freshSkipped;
        private static LootVisibilityTracker<ZDOID>? tracker;
        private static LootSendLegTracker<ZDOID>? ownerLeg, serverLeg;
        private static ConfigEntry<bool>? enabled;
        private static ConfigEntry<float>? radius;
        private static ConfigEntry<int>? windowSeconds;
        private static ManualLogSource logger = null!;
        // One bounded session reference permits cheap identity checks. Reset releases it;
        // no game object or world is retained by CaptureSession.
        private static CaptureSession? capture;
        private static WeakReference<ZNetScene>? scene;
        private static double installedRadius, installedWindowMs;
        private static long unknownPrefabs, intervalFailures, failures, legFailures, intervalLegFailures;
        private static bool legsEnabled, collecting;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal static string LegsStatus { get; private set; } = "disabled";

        private sealed class SourceInfo
        {
            internal readonly HashSet<int> Drops = new HashSet<int>();
            internal byte Kind;
            internal double Radius;
        }

        private struct FreshZdo
        {
            internal ZDO Zdo;
            internal double AtMs;
        }

        // Adapter over one peer's sent-ZDO map; bound for the duration of one check.
        private sealed class PeerSentSet : LootSendLegTracker<ZDOID>.ISentSet
        {
            internal ZdoPeerAccess.ISyncTimes? Times;
            internal object? Zdos;
            public bool Contains(ZDOID key) => Times != null && Zdos != null && Times.TryGet(Zdos, key, out _);
        }

        internal static void Install(ConfigFile config, ManualLogSource log)
        {
            logger = log;
            enabled = config.Bind("Diagnostics", "LootVisibilityEnabled", true,
                "Read-only sampled observation of the delay between a mined chunk disappearing and its loot becoming visible locally; attribution is by position, time and drop table, not identity. Clients observe; the owner and the server also time a new drop's first send.");
            radius = config.Bind("Diagnostics", "LootVisibilityRadius", 12f,
                new ConfigDescription("Arrival radius in metres around a destruction. Each source kind then applies its own, smaller spawn radius. A drop farther than this is counted as unattributed rather than assigned to the wrong chunk.", new AcceptableValueRange<float>(2f, 32f)));
            windowSeconds = config.Bind("Diagnostics", "LootVisibilityWindowSeconds", 5,
                new ConfigDescription("How long a destroyed hit area stays eligible for attribution. Loot arriving later is counted as unattributed, never as a long delay.", new AcceptableValueRange<int>(1, 15)));
            if (!enabled.Value) { Status = LegsStatus = "disabled"; return; }
            if (Dedicated()) { Status = "dedicated-server"; }
            try
            {
                var (areaHealth, createZdo, addInstance, destroy, zdoDestroyed) = ValidateContracts();
                installedRadius = Math.Max(2, Math.Min(32, radius.Value));
                installedWindowMs = Math.Max(1, Math.Min(15, windowSeconds.Value)) * 1000.0;
                tracker = new LootVisibilityTracker<ZDOID>(DestructionCapacity, ArrivalCapacity,
                    installedRadius, installedWindowMs, SpawnCheck);
                // A prefix: RPC_SetAreaHealth ends in UpdateMesh, which deactivates the destroyed
                // area's collider, and an inactive collider reports empty bounds.
                Patches.Patch(areaHealth, prefix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(BeforeSetAreaHealth)));
                Patches.Patch(createZdo, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterCreateNewZDO)));
                Patches.Patch(addInstance, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterAddInstance)));
                Patches.Patch(destroy, prefix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(BeforeDestroy)));
                Patches.Patch(zdoDestroyed, prefix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(BeforeZdoDestroyed)));
                Installed = Enabled = true;
                if (Status != "dedicated-server") Status = "installed";
            }
            catch (Exception exception)
            {
                Installed = Enabled = legsEnabled = false;
                Status = LegsStatus = "unavailable";
                tracker = null;
                try { Patches.UnpatchSelf(); } catch { failures++; }
                logger.LogWarning("Loot visibility diagnostics unavailable; native behaviour retained: " +
                    exception.GetType().Name + ": " + exception.Message);
                return;
            }
            InstallLegs();
        }

        // Separate so that losing the legs costs the legs, never the observer probe.
        private static void InstallLegs()
        {
            try
            {
                ZdoPeerAccess.Resolve();
                var send = AccessTools.DeclaredMethod(typeof(ZDOMan), "SendZDOs", new[] { ZdoPeerAccess.PeerType, typeof(bool) })
                    ?? throw new InvalidOperationException("ZDOMan.SendZDOs is missing.");
                if (send.IsStatic || send.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unsupported ZDOMan.SendZDOs signature.");
                var zdoData = AccessTools.DeclaredMethod(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) })
                    ?? throw new InvalidOperationException("ZDOMan.RPC_ZDOData is missing.");
                if (zdoData.IsStatic || zdoData.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported ZDOMan.RPC_ZDOData signature.");
                if (AccessTools.DeclaredMethod(typeof(ZDO), "GetOwner", Type.EmptyTypes)?.ReturnType != typeof(long) ||
                    AccessTools.DeclaredMethod(typeof(ZNetScene), "GetPrefab", new[] { typeof(int) })?.ReturnType != typeof(GameObject))
                    throw new InvalidOperationException("Unsupported ZDO owner or prefab lookup contract.");
                ownerLeg = new LootSendLegTracker<ZDOID>(LegCapacity, installedWindowMs, firstPeerCompletes: true);
                serverLeg = new LootSendLegTracker<ZDOID>(LegCapacity, installedWindowMs, firstPeerCompletes: false);
                Patches.Patch(send, postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterSendZdos)));
                Patches.Patch(zdoData,
                    prefix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(BeforeZdoData)),
                    postfix: new HarmonyMethod(typeof(LootVisibilityTelemetry), nameof(AfterZdoData)));
                legsEnabled = true;
                LegsStatus = "installed";
            }
            catch (Exception exception)
            {
                legsEnabled = false;
                ownerLeg = serverLeg = null;
                LegsStatus = "unavailable";
                logger.LogWarning("Loot visibility send legs unavailable; observer probe retained: " +
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        // ZNet does not exist yet during plugin Awake, so this is also re-tested from the
        // hooks once a world exists; a dedicated server then stops the observer.
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
            // Optional: without the area geometry t0 falls back to the rock root and the
            // arrival radius, counted in loot_visibility_area_centre_fallbacks.
            ResolveAreaGeometry();
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
            // Drop tables: the fields each source's destruction reads when it spawns loot.
            if (AccessTools.DeclaredField(typeof(MineRock5), "m_dropItems")?.FieldType != typeof(DropTable) ||
                AccessTools.DeclaredField(typeof(MineRock), "m_dropItems")?.FieldType != typeof(DropTable) ||
                AccessTools.DeclaredField(typeof(DropOnDestroyed), "m_dropWhenDestroyed")?.FieldType != typeof(DropTable) ||
                AccessTools.DeclaredField(typeof(TreeLog), "m_dropWhenDestroyed")?.FieldType != typeof(DropTable) ||
                AccessTools.DeclaredField(typeof(TreeBase), "m_dropWhenDestroyed")?.FieldType != typeof(DropTable) ||
                AccessTools.DeclaredField(typeof(DropTable), "m_drops")?.FieldType != typeof(List<DropTable.DropData>))
                throw new InvalidOperationException("Unsupported drop-table contract.");
            return (areaHealth, createZdo, addInstance, destroy, zdoDestroyed);
        }

        private static void ResolveAreaGeometry()
        {
            hitAreasField = areaColliderField = null;
            colliderBounds = colliderEnabled = null;
            var areas = AccessTools.DeclaredField(typeof(MineRock5), "m_hitAreas");
            var areaType = AccessTools.Inner(typeof(MineRock5), "HitArea");
            var collider = areaType == null ? null : AccessTools.DeclaredField(areaType, "m_collider");
            if (areas == null || areas.IsStatic || !typeof(IList).IsAssignableFrom(areas.FieldType) ||
                collider == null || collider.IsStatic || !typeof(Component).IsAssignableFrom(collider.FieldType)) return;
            var bounds = collider.FieldType.GetProperty("bounds", BindingFlags.Instance | BindingFlags.Public);
            var active = collider.FieldType.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (bounds?.PropertyType != typeof(Bounds) || active?.PropertyType != typeof(bool)) return;
            hitAreasField = areas;
            areaColliderField = collider;
            colliderBounds = bounds;
            colliderEnabled = active;
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

        private static bool BindCapture()
        {
            var session = System.Threading.Volatile.Read(ref TimingHooks.Current);
            if (session == null) return false;
            if (!ReferenceEquals(capture, session)) { Reset(); capture = session; }
            return true;
        }

        private static bool Bind(LootVisibilityTracker<ZDOID> current)
        {
            if (!BindCapture()) return false;
            // Unpatching from inside a running patch is unsafe; stop observing instead and
            // let the plugin's own shutdown remove the patches.
            if (Dedicated()) { Enabled = false; Status = "dedicated-server"; current.Clear(false); return false; }
            var currentScene = ZNetScene.instance;
            if (currentScene == null) return false;
            if (scene == null || !scene.TryGetTarget(out var previous) || !ReferenceEquals(previous, currentScene))
            {
                current.Clear(true);
                ownerLeg?.Clear(true);
                PrefabKinds.Clear();
                Sources.Clear();
                scene = new WeakReference<ZNetScene>(currentScene);
            }
            return true;
        }

        // The send leg that applies to this process, or null: a client follows its own new
        // drops to the server, the server follows drops it received to every other peer.
        private static LootSendLegTracker<ZDOID>? Legs(bool server)
        {
            if (!legsEnabled || enabled == null || !enabled.Value) return null;
            if (System.Threading.Volatile.Read(ref TimingHooks.Current) == null) return null;
            var net = ZNet.instance;
            if (ReferenceEquals(net, null) || net.IsServer() != server) return null;
            return server ? serverLeg : ownerLeg;
        }

        private static void BeforeSetAreaHealth(MineRock5 __instance, long __0, int __1, float __2)
        {
            var current = Observing();
            if (current == null || !(__2 <= 0) || __instance == null) return;
            try
            {
                if (!Bind(current)) return;
                var view = __instance.GetComponent<ZNetView>();
                var zdo = view != null ? view.GetZDO() : null;
                int hash = zdo != null ? zdo.GetPrefab() : 0;
                if (!TryAreaSource(hash, __instance)) return;
                // Drops spawn at the area centre (native) or on its surface (hit-point
                // placement on the owner), so the spread is the area's half-diagonal.
                Vector3 position;
                double spread;
                if (AreaBounds(__instance, __1, out var bounds))
                {
                    position = bounds.center;
                    spread = Math.Max(MinimumSpreadMetres, bounds.extents.magnitude + SettleMetres);
                }
                else
                {
                    areaCentreFallbacks++;
                    position = __instance.transform.position;
                    spread = installedRadius;
                }
                // The routed sender is the process that ran DamageArea and instantiated the drops.
                bool owned = __0 == ZDOMan.GetSessionID();
                // A destroyed MineRock5 area is a rock event; Record only sees the legacy
                // MineRock component, whose object is destroyed outright.
                destroyedRock++;
                current.Destroyed(position.x, position.y, position.z, NowMs(), hash, KindArea, owned, spread);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static bool AreaBounds(MineRock5 rock, int index, out Bounds bounds)
        {
            bounds = default;
            if (hitAreasField == null || areaColliderField == null || colliderBounds == null || colliderEnabled == null) return false;
            if (!(hitAreasField.GetValue(rock) is IList areas) || index < 0 || index >= areas.Count) return false;
            object? area = areas[index];
            if (area == null || !(areaColliderField.GetValue(area) is Component collider) || collider == null) return false;
            if (!collider.gameObject.activeInHierarchy || !(colliderEnabled.GetValue(collider) is bool on) || !on) return false;
            if (!(colliderBounds.GetValue(collider) is Bounds read) || read.extents.sqrMagnitude <= 0) return false;
            bounds = read;
            return true;
        }

        private static void AfterCreateNewZDO(ZDOID __0, Vector3 __1, int __2, ZDO __result)
        {
            // A non-zero prefab hash means this process created the ZDO itself; only the
            // RPC_ZDOData path leaves it zero.
            if (__2 != 0) return;
            if (collecting && __result != null)
            {
                // Server leg: classified after RPC_ZDOData has deserialized the prefab.
                if (Fresh.Count < FreshLimit) Fresh.Add(new FreshZdo { Zdo = __result, AtMs = NowMs() });
                else freshSkipped++;
            }
            var current = Observing();
            // No pending destruction means no work at all.
            if (current == null || current.PendingDestructions == 0) return;
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
                double now = NowMs();
                current.Created(__0.m_uid, position.x, position.y, position.z, now, __0.IsOwner(), hash, SpawnAgeMs(__0), out bool ownerMatched);
                if (ownerMatched) Legs(server: false)?.Start(__0.m_uid, 0, now);
            }
            catch (Exception exception) { Fail(exception); }
        }

        // ItemDrop.Awake stamps a new drop with the synced game time on its owner; a log
        // carries no stamp. NaN is unknown, never zero.
        private static double SpawnAgeMs(ZDO zdo)
        {
            var net = ZNet.instance;
            long spawned = zdo.GetLong(ZDOVars.s_spawnTime, 0L);
            if (ReferenceEquals(net, null) || spawned <= 0) return double.NaN;
            return (net.GetTime().Ticks - spawned) / (double)TimeSpan.TicksPerMillisecond;
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
                Record(current, zdo.GetPrefab(), __0, __0.transform.position, owned: true);
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
                Record(current, __0.GetPrefab(), view.gameObject, __0.GetPosition(), owned: false);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void Record(LootVisibilityTracker<ZDOID> current, int hash, GameObject go, Vector3 position, bool owned)
        {
            if (!Sources.TryGetValue(hash, out var source))
            {
                if (Sources.Count >= PrefabCapacity) { unknownPrefabs++; return; }
                source = Describe(go);
                Sources.Add(hash, source);
            }
            if (source == null) return;
            switch (source.Kind)
            {
                case KindRock: destroyedRock++; break;
                case KindTree: destroyedTree++; break;
                case KindLog: destroyedLog++; break;
                case KindFracture: destroyedFracture++; break;
                default: destroyedDestructible++; break;
            }
            current.Destroyed(position.x, position.y, position.z, NowMs(), hash, source.Kind, owned, source.Radius);
        }

        private static bool TryAreaSource(int hash, MineRock5 rock)
        {
            if (Sources.TryGetValue(hash, out var source)) return source != null;
            if (Sources.Count >= PrefabCapacity) { unknownPrefabs++; return false; }
            source = new SourceInfo { Kind = KindArea, Radius = installedRadius };
            AddTable(source, rock.m_dropItems);
            Sources.Add(hash, Counted(source));
            return true;
        }

        // What a destroyed object can spawn and how far from its root, read from the spawn
        // code of each source: TreeBase.RPC_Damage, TreeLog.Destroy, MineRock.RPC_Hit,
        // DropOnDestroyed.OnDestroyed and Destructible.Destroy's m_spawnWhenDestroyed.
        private static SourceInfo? Describe(GameObject go)
        {
            var root = go.transform.position;
            var tree = go.GetComponent<TreeBase>();
            if (tree != null)
            {
                var source = new SourceInfo { Kind = KindTree };
                AddTable(source, tree.m_dropWhenDestroyed);
                AddPrefab(source, tree.m_logPrefab);
                double log = tree.m_logSpawnPoint != null ? Vector3.Distance(tree.m_logSpawnPoint.position, root) : 0;
                source.Radius = Spread(Math.Max(Stacked(tree.m_dropWhenDestroyed, tree.m_spawnYOffset, tree.m_spawnYStep), log));
                return Counted(source);
            }
            var treeLog = go.GetComponent<TreeLog>();
            if (treeLog != null)
            {
                var source = new SourceInfo { Kind = KindLog };
                AddTable(source, treeLog.m_dropWhenDestroyed);
                // TreeLog.Destroy passes each drop through Game.CheckDropConversion.
                AddConversions(source);
                AddPrefab(source, treeLog.m_subLogPrefab);
                double spread = Math.Abs(treeLog.m_spawnDistance) + 0.3 * MaxItems(treeLog.m_dropWhenDestroyed);
                if (treeLog.m_subLogPrefab != null && treeLog.m_subLogPoints != null)
                    foreach (var point in treeLog.m_subLogPoints)
                        if (point != null) spread = Math.Max(spread, Vector3.Distance(point.position, root));
                source.Radius = Spread(spread);
                return Counted(source);
            }
            var rock = go.GetComponent<MineRock>();
            if (rock != null)
            {
                // Drops at the hit point, which can be anywhere on the rock.
                var source = new SourceInfo { Kind = KindRock, Radius = installedRadius };
                AddTable(source, rock.m_dropItems);
                return Counted(source);
            }
            var destructible = go.GetComponent<Destructible>();
            if (destructible == null) return null;
            var dropper = go.GetComponent<DropOnDestroyed>();
            // rock4_copper -> rock4_copper_frac: the spawned MineRock5 is damaged at once and
            // drops from its first area before any client has its instance, so that area's
            // RPC_SetAreaHealth reaches nobody; this destruction is the only t0 they get.
            var fracture = destructible.m_spawnWhenDestroyed != null ? destructible.m_spawnWhenDestroyed.GetComponent<MineRock5>() : null;
            if (dropper == null && fracture == null) return null;
            var result = new SourceInfo { Kind = dropper != null ? KindDestructible : KindFracture };
            if (dropper != null)
            {
                AddTable(result, dropper.m_dropWhenDestroyed);
                result.Radius = Spread(Stacked(dropper.m_dropWhenDestroyed, dropper.m_spawnYOffset, dropper.m_spawnYStep));
            }
            if (fracture != null)
            {
                AddTable(result, fracture.m_dropItems);
                result.Radius = installedRadius;
            }
            return Counted(result);
        }

        private static SourceInfo Counted(SourceInfo source)
        {
            if (source.Drops.Count == 0) emptyTables++;
            return source;
        }

        // Vertical stacking at spawnYStep per item above a 0.5 m disc, as TreeBase and
        // DropOnDestroyed place their drops.
        private static double Stacked(DropTable? table, float yOffset, float yStep) =>
            0.5 + Math.Abs(yOffset) + Math.Abs(yStep) * Math.Max(0, MaxItems(table) - 1);

        private static double Spread(double spawnSpread) =>
            Math.Min(installedRadius, Math.Max(MinimumSpreadMetres, spawnSpread + SettleMetres));

        // GetDropList repeats the table Ceil(resourceRate) times and expands every stack into
        // single items; bounded so an extreme table cannot claim the whole arrival radius.
        private static double MaxItems(DropTable? table)
        {
            if (table?.m_drops == null || table.m_drops.Count == 0) return 0;
            int stack = 1;
            foreach (var drop in table.m_drops) stack = Math.Max(stack, drop.m_stackMax);
            double rate = Math.Max(1, Math.Ceiling(Game.m_resourceRate));
            return Math.Min(MaxDropItems, rate * Math.Max(1, table.m_dropMax) * stack);
        }

        private static void AddTable(SourceInfo source, DropTable? table)
        {
            if (table?.m_drops == null) return;
            foreach (var drop in table.m_drops) AddPrefab(source, drop.m_item);
        }

        private static void AddPrefab(SourceInfo source, GameObject? prefab)
        {
            if (prefab != null) source.Drops.Add(Utils.GetPrefabName(prefab.name).GetStableHashCode());
        }

        private static void AddConversions(SourceInfo source)
        {
            var game = Game.instance;
            if (game == null || game.m_damageTypeDropConversions == null) return;
            foreach (var conversion in game.m_damageTypeDropConversions)
            {
                if (conversion?.m_items == null || conversion.m_result == null) continue;
                foreach (var item in conversion.m_items)
                {
                    if (item == null || !source.Drops.Contains(Utils.GetPrefabName(item.gameObject.name).GetStableHashCode())) continue;
                    AddPrefab(source, conversion.m_result.gameObject);
                    break;
                }
            }
        }

        private static bool CanSpawn(int source, int drop) =>
            Sources.TryGetValue(source, out var info) && info != null && info.Drops.Contains(drop);

        private static void BeforeZdoData()
        {
            collecting = false;
            Fresh.Clear();
            if (Legs(server: true) == null) return;
            try { collecting = BindCapture(); }
            catch (Exception exception) { LegFail(exception); }
        }

        // First receipt of a new drop on the server. The prefab is known only now, after
        // RPC_ZDOData deserialized every ZDO the package carried.
        private static void AfterZdoData()
        {
            if (!collecting) return;
            collecting = false;
            try
            {
                var legs = Legs(server: true);
                if (legs == null) return;
                foreach (var fresh in Fresh)
                    if (fresh.Zdo != null && IsLoot(fresh.Zdo.GetPrefab()))
                        legs.Start(fresh.Zdo.m_uid, fresh.Zdo.GetOwner(), fresh.AtMs);
            }
            catch (Exception exception) { LegFail(exception); }
            finally { Fresh.Clear(); }
        }

        // A server has no instance to ask, so the prefab answers the loot question.
        private static bool IsLoot(int hash)
        {
            if (PrefabKinds.TryGetValue(hash, out bool loot)) return loot;
            if (PrefabKinds.Count >= PrefabCapacity) { unknownPrefabs++; return false; }
            var netScene = ZNetScene.instance;
            var prefab = netScene != null ? netScene.GetPrefab(hash) : null;
            loot = prefab != null && (prefab.GetComponent<ItemDrop>() != null || prefab.GetComponent<TreeLog>() != null);
            PrefabKinds.Add(hash, loot);
            return loot;
        }

        // After a ZDOData send to one peer: SendZDOs records every ZDO it wrote in that
        // peer's sent map, so a followed drop found there has just been sent to it.
        private static void AfterSendZdos(object __0, bool __result)
        {
            if (!__result || !legsEnabled) return;
            if ((ownerLeg?.PendingCount ?? 0) == 0 && (serverLeg?.PendingCount ?? 0) == 0) return;
            try
            {
                var net = ZNet.instance;
                var legs = ReferenceEquals(net, null) ? null : Legs(net.IsServer());
                if (legs == null || legs.PendingCount == 0 || __0 == null) return;
                var times = ZdoPeerAccess.SyncTimes;
                object? zdos = ZdoPeerAccess.Zdos(__0);
                var peer = ZdoPeerAccess.NetPeer(__0);
                if (times == null || zdos == null || peer == null) return;
                SentSet.Times = times;
                SentSet.Zdos = zdos;
                legs.Sent(peer.m_uid, SentSet, NowMs());
            }
            catch (Exception exception) { LegFail(exception); }
            finally { SentSet.Times = null; SentSet.Zdos = null; }
        }

        private static void LegFail(Exception exception)
        {
            legFailures++;
            intervalLegFailures++;
            if (legFailures < FailureLimit) return;
            legsEnabled = collecting = false;
            LegsStatus = "failed";
            ownerLeg?.Clear(true);
            serverLeg?.Clear(true);
            Fresh.Clear();
            logger.LogWarning("Loot visibility send legs disabled after repeated failures: " + exception.GetType().Name);
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
            Sources.Clear();
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
                "t2_is_ZNetScene.AddInstance; arrival_missing_is_unmeasured; area_t0_is_hit_area_centre; owned_sources_excluded"));
            labels.Add(new TextValue("loot_visibility_attribution", "network_arrival_table_filtered_v3"));
            labels.Add(new TextValue("loot_visibility_legs_status", LegsStatus));
            gauges.Add(new NumberValue("loot_visibility_probe_failures", intervalFailures, "calls"));
            gauges.Add(new NumberValue("loot_visibility_unknown_prefabs", unknownPrefabs, "observations"));
            intervalFailures = unknownPrefabs = 0;
            SampleLegs(gauges);
            // The observer never runs on a dedicated server: its gauges would be zeros that
            // look measured, so only the status and the send leg are exported there.
            if (Status == "dedicated-server") return;
            gauges.Add(new NumberValue("loot_visibility_destroyed_rock", destroyedRock, "events"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_tree", destroyedTree, "events"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_log", destroyedLog, "events"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_destructible", destroyedDestructible, "events"));
            gauges.Add(new NumberValue("loot_visibility_destroyed_fracture", destroyedFracture, "events"));
            gauges.Add(new NumberValue("loot_visibility_area_centre_fallbacks", areaCentreFallbacks, "events"));
            gauges.Add(new NumberValue("loot_visibility_source_empty_tables", emptyTables, "prefabs"));
            destroyedRock = destroyedTree = destroyedLog = destroyedDestructible = destroyedFracture = 0;
            areaCentreFallbacks = emptyTables = 0;
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
            gauges.Add(new NumberValue("loot_visibility_perceived_over_1s", summary.PerceivedOverOneSecond, "observations"));
            var bounds = LootVisibilityTracker<ZDOID>.BucketUpperBoundsMs;
            for (int i = 0; i < LootVisibilityTracker<ZDOID>.BucketCount; i++)
                gauges.Add(new NumberValue("loot_visibility_perceived_bucket_" +
                    (i < bounds.Length ? bounds[i].ToString("0", CultureInfo.InvariantCulture) : "over"),
                    summary.PerceivedBuckets[i], "observations"));
            gauges.Add(new NumberValue("loot_visibility_unattributed", summary.Unattributed, "observations"));
            gauges.Add(new NumberValue("loot_visibility_arrival_missing", summary.ArrivalMissing, "observations"));
            gauges.Add(new NumberValue("loot_visibility_ambiguous", summary.Ambiguous, "observations"));
            gauges.Add(new NumberValue("loot_visibility_foreign", summary.Foreign, "observations"));
            gauges.Add(new NumberValue("loot_visibility_out_of_radius", summary.OutOfRadius, "observations"));
            gauges.Add(new NumberValue("loot_visibility_owned_only", summary.OwnedOnly, "observations"));
            gauges.Add(new NumberValue("loot_visibility_owned_source_excluded", summary.OwnedSourceExcluded, "observations"));
            gauges.Add(new NumberValue("loot_visibility_candidates_truncated", summary.CandidatesTruncated, "observations"));
            gauges.Add(new NumberValue("loot_visibility_stale", summary.Stale, "observations"));
            gauges.Add(new NumberValue("loot_visibility_locally_owned", summary.LocallyOwned, "observations"));
            gauges.Add(new NumberValue("loot_visibility_owner_instantiate_count", summary.OwnerInstantiateCount, "observations"));
            gauges.Add(new NumberValue("loot_visibility_owner_instantiate_sum", summary.OwnerInstantiateSumMs, "ms"));
            gauges.Add(new NumberValue("loot_visibility_owner_instantiate_max", summary.OwnerInstantiateMaxMs, "ms"));
            gauges.Add(new NumberValue("loot_visibility_owner_unmatched", summary.OwnerUnmatched, "observations"));
            gauges.Add(new NumberValue("loot_visibility_owner_ambiguous", summary.OwnerAmbiguous, "observations"));
            gauges.Add(new NumberValue("loot_visibility_censored", summary.Censored, "entries"));
            gauges.Add(new NumberValue("loot_visibility_destruction_overflow", summary.Overflowed, "destructions"));
            gauges.Add(new NumberValue("loot_visibility_arrival_capacity_skips", summary.ArrivalCapacitySkipped, "observations"));
            gauges.Add(new NumberValue("loot_visibility_non_monotonic", summary.NonMonotonic, "durations"));
            gauges.Add(new NumberValue("loot_visibility_arrivals_expired", summary.ArrivalsExpired, "arrivals"));
            gauges.Add(new NumberValue("loot_visibility_pending_destructions", summary.PendingDestructions, "destructions"));
            gauges.Add(new NumberValue("loot_visibility_pending_arrivals", summary.PendingArrivals, "arrivals"));
            gauges.Add(new NumberValue("loot_visibility_witness_count", summary.Witnesses.Length, "observations"));
            gauges.Add(new NumberValue("loot_visibility_witness_overflow", summary.WitnessOverflow, "observations"));
            if (summary.Witnesses.Length > 0)
                labels.Add(new TextValue("loot_visibility_witness", FormatWitnesses(summary.Witnesses)));
        }

        // Only the leg this process runs is exported; the other is unmeasured here, not zero.
        private static void SampleLegs(List<NumberValue> gauges)
        {
            gauges.Add(new NumberValue("loot_visibility_leg_failures", intervalLegFailures, "calls"));
            intervalLegFailures = 0;
            var net = ZNet.instance;
            if (!legsEnabled || ReferenceEquals(net, null)) return;
            bool server = net.IsServer();
            var legs = server ? serverLeg : ownerLeg;
            if (legs == null) return;
            var summary = legs.Drain(NowMs());
            string prefix = server ? "loot_visibility_server_send_" : "loot_visibility_owner_send_";
            gauges.Add(new NumberValue(prefix + "count", summary.Count, "observations"));
            gauges.Add(new NumberValue(prefix + "sum", summary.SumMs, "ms"));
            gauges.Add(new NumberValue(prefix + "max", summary.MaxMs, "ms"));
            gauges.Add(new NumberValue(prefix + "over_1s", summary.OverOneSecond, "observations"));
            gauges.Add(new NumberValue(prefix + "started", summary.Started, "drops"));
            gauges.Add(new NumberValue(prefix + "skipped", summary.Skipped, "drops"));
            gauges.Add(new NumberValue(prefix + "expired_unsent", summary.ExpiredUnsent, "drops"));
            gauges.Add(new NumberValue(prefix + "non_monotonic", summary.NonMonotonic, "durations"));
            gauges.Add(new NumberValue(prefix + "censored", summary.Censored, "drops"));
            gauges.Add(new NumberValue(prefix + "pending", summary.Pending, "drops"));
            if (!server) return;
            gauges.Add(new NumberValue(prefix + "peer_slots_full", summary.PeerSlotsFull, "drops"));
            gauges.Add(new NumberValue(prefix + "scan_skipped", freshSkipped, "zdos"));
            freshSkipped = 0;
        }

        // drop<kind:source,d=m,net=ms,cre=ms,cand=before/after_table,own=excluded; ...
        private static string FormatWitnesses(LootVisibilityTracker<ZDOID>.Witness[] witnesses)
        {
            var text = new StringBuilder(witnesses.Length * 96);
            foreach (var witness in witnesses)
            {
                if (text.Length > 0) text.Append(';');
                text.Append(PrefabName(witness.DropPrefab)).Append('<')
                    .Append(witness.SourceKind < KindNames.Length ? KindNames[witness.SourceKind] : "none").Append(':')
                    .Append(PrefabName(witness.SourcePrefab))
                    .Append(",d=").Append(witness.DistanceMetres.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(",net=").Append(witness.NetworkMs.ToString("0", CultureInfo.InvariantCulture))
                    .Append(",cre=").Append(witness.CreationMs.ToString("0", CultureInfo.InvariantCulture))
                    .Append(",cand=").Append(witness.CandidatesBefore.ToString(CultureInfo.InvariantCulture))
                    .Append('/').Append(witness.CandidatesAfterTable.ToString(CultureInfo.InvariantCulture))
                    .Append(",own=").Append(witness.OwnedExcluded.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        // Names are resolved on the main thread at export, never inside a hook.
        private static string PrefabName(int hash)
        {
            if (PrefabNames.TryGetValue(hash, out string cached)) return cached;
            string name = "prefab_" + hash.ToString(CultureInfo.InvariantCulture);
            try
            {
                var netScene = ZNetScene.instance;
                var prefab = netScene != null ? netScene.GetPrefab(hash) : null;
                if (prefab != null && !string.IsNullOrEmpty(prefab.name)) name = Sanitize(prefab.name);
            }
            catch { failures++; }
            if (PrefabNames.Count < NameCapacity) PrefabNames[hash] = name;
            return name;
        }

        // The witness grammar uses ; , < : = as separators.
        private static string Sanitize(string name)
        {
            var clean = new StringBuilder(Math.Min(name.Length, NameLength));
            foreach (char c in name)
            {
                if (clean.Length >= NameLength) break;
                clean.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            }
            return clean.Length == 0 ? "unnamed" : clean.ToString();
        }

        internal static void Reset()
        {
            tracker?.Clear(false);
            ownerLeg?.Clear(false);
            serverLeg?.Clear(false);
            PrefabKinds.Clear();
            Sources.Clear();
            PrefabNames.Clear();
            Fresh.Clear();
            collecting = false;
            destroyedRock = destroyedTree = destroyedLog = destroyedDestructible = destroyedFracture = 0;
            areaCentreFallbacks = emptyTables = freshSkipped = 0;
            capture = null;
            scene = null;
            unknownPrefabs = intervalFailures = intervalLegFailures = 0;
        }

        internal static void Uninstall()
        {
            Reset();
            failures = legFailures = 0;
            installedRadius = installedWindowMs = 0;
            try { Patches.UnpatchSelf(); } catch { }
            tracker = null;
            ownerLeg = serverLeg = null;
            hitAreasField = areaColliderField = null;
            colliderBounds = colliderEnabled = null;
            Installed = Enabled = legsEnabled = false;
            Status = LegsStatus = "disabled";
            enabled = null;
            radius = null;
            windowSeconds = null;
        }
    }
}
