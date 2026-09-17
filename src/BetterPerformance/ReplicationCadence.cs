using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Reflective access to ZDOMan's private nested ZDOPeer. The three fields are read
    // once per selection pass, not once per object, and the per-object sync time is
    // read through a generic shim so the private info struct is never boxed.
    internal static class ZdoPeerAccess
    {
        internal interface ISyncTimes { bool TryGet(object dictionary, ZDOID uid, out float syncTime); }

        private sealed class SyncTimeReader<TInfo> : ISyncTimes where TInfo : struct
        {
            private readonly Func<TInfo, float> read;
            internal SyncTimeReader(Delegate reader) { read = (Func<TInfo, float>)reader; }
            public bool TryGet(object dictionary, ZDOID uid, out float syncTime)
            {
                if (dictionary is Dictionary<ZDOID, TInfo> typed && typed.TryGetValue(uid, out TInfo info))
                {
                    syncTime = read(info);
                    return true;
                }
                syncTime = 0f;
                return false;
            }
        }

        private static FieldInfo? zdosField, forceSendField, peerField;

        internal static Type PeerType { get; private set; } = typeof(object);
        internal static ISyncTimes? SyncTimes { get; private set; }
        internal static bool Resolved { get; private set; }

        internal static void Resolve()
        {
            if (Resolved) return;
            Type peerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer")
                ?? throw new InvalidOperationException("ZDOMan.ZDOPeer is missing.");
            FieldInfo zdos = AccessTools.DeclaredField(peerType, "m_zdos")
                ?? throw new InvalidOperationException("ZDOPeer.m_zdos is missing.");
            FieldInfo forceSend = AccessTools.DeclaredField(peerType, "m_forceSend")
                ?? throw new InvalidOperationException("ZDOPeer.m_forceSend is missing.");
            FieldInfo peer = AccessTools.DeclaredField(peerType, "m_peer")
                ?? throw new InvalidOperationException("ZDOPeer.m_peer is missing.");
            if (forceSend.FieldType != typeof(HashSet<ZDOID>) || peer.FieldType != typeof(ZNetPeer))
                throw new InvalidOperationException("Unsupported ZDOPeer field types.");
            if (!zdos.FieldType.IsGenericType || zdos.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
                throw new InvalidOperationException("Unsupported ZDOPeer.m_zdos container.");
            Type[] arguments = zdos.FieldType.GetGenericArguments();
            if (arguments[0] != typeof(ZDOID) || !arguments[1].IsValueType)
                throw new InvalidOperationException("Unsupported ZDOPeer.m_zdos key or value type.");
            Type infoType = arguments[1];
            FieldInfo syncTime = AccessTools.DeclaredField(infoType, "m_syncTime")
                ?? throw new InvalidOperationException("PeerZDOInfo.m_syncTime is missing.");
            if (syncTime.FieldType != typeof(float) || syncTime.IsStatic)
                throw new InvalidOperationException("Unsupported PeerZDOInfo.m_syncTime type.");
            var reader = new DynamicMethod("BetterPerformanceReadSyncTime", typeof(float), new[] { infoType }, infoType.Module, true);
            ILGenerator il = reader.GetILGenerator();
            il.Emit(OpCodes.Ldarga_S, (byte)0);
            il.Emit(OpCodes.Ldfld, syncTime);
            il.Emit(OpCodes.Ret);
            Delegate accessor = reader.CreateDelegate(typeof(Func<,>).MakeGenericType(infoType, typeof(float)));
            SyncTimes = (ISyncTimes)Activator.CreateInstance(typeof(SyncTimeReader<>).MakeGenericType(infoType),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { accessor }, null);
            zdosField = zdos;
            forceSendField = forceSend;
            peerField = peer;
            PeerType = peerType;
            Resolved = true;
        }

        internal static object? Zdos(object peer) => zdosField?.GetValue(peer);
        internal static HashSet<ZDOID>? ForceSend(object peer) => forceSendField?.GetValue(peer) as HashSet<ZDOID>;
        internal static ZNetPeer? NetPeer(object peer) => peerField?.GetValue(peer) as ZNetPeer;

        internal static void Clear()
        {
            zdosField = forceSendField = peerField = null;
            SyncTimes = null;
            PeerType = typeof(object);
            Resolved = false;
        }
    }

    // Two opt-in, default-off replication changes, both confined to what one process
    // owns. Neither touches the wire format, ZDO.Serialize, revision accounting,
    // ownership or persistence.
    //
    // A. A minimum resend interval for named cosmetic prefabs. Deferring is lossless:
    //    serialization is always full current state, never a delta, and the entry stays
    //    queued, so the next send carries everything the skipped one would have.
    // B. A published velocity for flying birds. RandomFlyingBird moves its transform
    //    directly and has no Rigidbody, so vanilla ZSyncTransform.GetVelocity returns
    //    zero and non-owners interpolate without extrapolating.
    internal static class ReplicationCadence
    {
        private const int ListLimit = 65536, BirdCapacity = 512;
        private const float StillSeconds = 0.15f, StaleSeconds = 0.5f, MaxSpeed = 40f;
        private const float MovementEpsilonSquared = 1e-8f, PublishEpsilonSquared = 0.0025f;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".ReplicationCadence");
        private static readonly HashSet<int> CosmeticPrefabs = new HashSet<int>();
        private static readonly List<ZDO> Kept = new List<ZDO>(256);
        private static readonly Dictionary<int, bool> BirdKinds = new Dictionary<int, bool>(BirdCapacity);
        private static readonly Dictionary<int, BirdMotion> BirdMotions = new Dictionary<int, BirdMotion>(BirdCapacity);
        private static ConfigEntry<bool> cadenceEnabled = null!, birdVelocityEnabled = null!;
        private static ConfigEntry<string> cosmeticNames = null!;
        private static ConfigEntry<float> minimumInterval = null!;
        private static ResendPolicy? policy;
        private static FieldInfo? bodyField;
        private static FieldInfo? viewField;
        private static ManualLogSource logger = null!;
        private static WeakReference<ZNetScene>? scene;
        private static bool cadencePatched, birdPatched, cadenceFailed, birdFailed;
        private static int configuredNames, knownPrefabs, unknownPrefabs;
        private static long passes, boundedSkips, removedEntries, birdPublications, birdResets, birdBoundedSkips;

        internal static bool Enabled { get; set; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal static bool CadenceActive => Installed && Enabled && cadencePatched && !cadenceFailed && cadenceEnabled != null && cadenceEnabled.Value;
        internal static bool BirdVelocityActive => Installed && Enabled && birdPatched && !birdFailed && birdVelocityEnabled != null && birdVelocityEnabled.Value;

        private struct BirdMotion
        {
            internal Vector3 LastPosition, Published;
            internal float LastMoveTime;
            internal bool HasPublished;
        }

        internal static void Install(ConfigFile config, ManualLogSource log)
        {
            logger = log;
            cadenceEnabled = config.Bind("Replication", "CosmeticResendIntervalEnabled", false,
                "Experimental. Apply a minimum resend interval to owned cosmetic prefabs on the send path. Deferring is lossless; the next send carries full state. Requires restart to install. Unmeasured.");
            cosmeticNames = config.Bind("Replication", "CosmeticPrefabs",
                "Fish1,Fish2,Fish3,Fish4,Fish5,Fish6,Fish7,Fish8,Fish9,Fish10,Fish11,Fish12,Crow,Seagal",
                "Comma-separated prefab names the resend interval applies to. Names absent from the loaded scene are counted and ignored.");
            minimumInterval = config.Bind("Replication", "CosmeticMinimumIntervalSeconds", 0.2f,
                new ConfigDescription("Minimum seconds between two sends of the same cosmetic object to the same peer. 0.2 is five per second against the vanilla twenty.",
                    new AcceptableValueRange<float>((float)ResendPolicy.MinimumIntervalSeconds, (float)ResendPolicy.MaximumIntervalSeconds)));
            birdVelocityEnabled = config.Bind("Replication", "BirdVelocityEnabled", false,
                "Experimental. Publish an estimated velocity for flying birds so remote clients extrapolate between updates. Owner-side only; remote clients need no plugin. Requires restart to install. Unmeasured.");
            if (!cadenceEnabled.Value && !birdVelocityEnabled.Value)
            {
                Status = "disabled";
                return;
            }
            if (cadenceEnabled.Value) Guarded(InstallCadence, "Cosmetic resend interval");
            if (birdVelocityEnabled.Value) Guarded(InstallBirdVelocity, "Bird velocity publication");
            Installed = cadencePatched || birdPatched;
            Enabled = Installed;
            Status = !Installed ? "unavailable"
                : cadencePatched && birdPatched ? "installed"
                : cadencePatched ? "installed_resend_interval_only" : "installed_bird_velocity_only";
        }

        // A missing game type surfaces when the installer is first entered, not inside
        // its own try block, so the call itself is guarded as well.
        private static void Guarded(Action install, string what)
        {
            try { install(); }
            catch (Exception exception) { logger.LogWarning(what + " unavailable: " + exception.GetType().Name + ": " + exception.Message); }
        }

        private static void InstallCadence()
        {
            try
            {
                ZdoPeerAccess.Resolve();
                MethodInfo method = AccessTools.DeclaredMethod(typeof(ZDOMan), "CreateSyncList",
                    new[] { ZdoPeerAccess.PeerType, typeof(List<ZDO>) })
                    ?? throw new InvalidOperationException("ZDOMan.CreateSyncList is missing.");
                if (method.ReturnType != typeof(void) || method.IsStatic)
                    throw new InvalidOperationException("Unsupported CreateSyncList signature.");
                policy = new ResendPolicy(Clamp(minimumInterval.Value));
                // Runs after the telemetry postfix, which must observe the untouched list.
                Patches.Patch(method, postfix: new HarmonyMethod(typeof(ReplicationCadence), nameof(AfterCreateSyncList)) { priority = Priority.Last });
                cadencePatched = true;
                logger.LogInfo("Cosmetic resend interval installed at " + policy.IntervalSeconds.ToString("0.###") + " s.");
            }
            catch (Exception exception)
            {
                cadencePatched = false;
                cadenceFailed = true;
                logger.LogWarning("Cosmetic resend interval unavailable; vanilla cadence retained: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void InstallBirdVelocity()
        {
            try
            {
                MethodInfo owner = AccessTools.DeclaredMethod(typeof(ZSyncTransform), "OwnerSync", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("ZSyncTransform.OwnerSync is missing.");
                MethodInfo disable = AccessTools.DeclaredMethod(typeof(ZSyncTransform), "OnDisable", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("ZSyncTransform.OnDisable is missing.");
                if (owner.ReturnType != typeof(void) || disable.ReturnType != typeof(void) || owner.IsStatic || disable.IsStatic)
                    throw new InvalidOperationException("Unsupported ZSyncTransform signatures.");
                FieldInfo? rigidbody = AccessTools.DeclaredField(typeof(ZSyncTransform), "m_body");
                // Named by full name, not by type: the plugin does not reference
                // UnityEngine.PhysicsModule and never touches physics state.
                FieldInfo? netView = AccessTools.DeclaredField(typeof(ZSyncTransform), "m_nview");
                if (rigidbody == null || rigidbody.FieldType.FullName != "UnityEngine.Rigidbody" ||
                    netView == null || netView.FieldType != typeof(ZNetView) ||
                    AccessTools.DeclaredField(typeof(ZSyncTransform), "m_syncPosition")?.FieldType != typeof(bool))
                    throw new InvalidOperationException("Unsupported ZSyncTransform fields.");
                if (AccessTools.DeclaredField(typeof(RandomFlyingBird), "m_speed")?.FieldType != typeof(float))
                    throw new InvalidOperationException("Unsupported RandomFlyingBird contract.");
                bodyField = rigidbody;
                viewField = netView;
                Patches.Patch(owner, postfix: new HarmonyMethod(typeof(ReplicationCadence), nameof(AfterOwnerSync)) { priority = Priority.Last });
                Patches.Patch(disable, postfix: new HarmonyMethod(typeof(ReplicationCadence), nameof(AfterDisable)));
                birdPatched = true;
                logger.LogInfo("Bird velocity publication installed; owner-side only.");
            }
            catch (Exception exception)
            {
                birdPatched = false;
                birdFailed = true;
                logger.LogWarning("Bird velocity publication unavailable: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static double Clamp(float seconds) =>
            float.IsNaN(seconds) ? 0.2 : Math.Min(ResendPolicy.MaximumIntervalSeconds, Math.Max(ResendPolicy.MinimumIntervalSeconds, seconds));

        // Part A. The decision pass never mutates, so a failure leaves the vanilla list
        // intact; only the replacement below changes it, and it cannot throw.
        private static void AfterCreateSyncList(object __0, List<ZDO> __1)
        {
            if (!CadenceActive || __1 == null || __1.Count == 0) return;
            try
            {
                if (__1.Count > ListLimit) { boundedSkips++; return; }
                ResolvePrefabs();
                if (CosmeticPrefabs.Count == 0) return;
                var syncTimes = ZdoPeerAccess.SyncTimes;
                object? zdos = ZdoPeerAccess.Zdos(__0);
                HashSet<ZDOID>? forced = ZdoPeerAccess.ForceSend(__0);
                if (syncTimes == null || zdos == null || policy == null) return;
                double interval = Clamp(minimumInterval.Value);
                if (Math.Abs(interval - policy.IntervalSeconds) > 1e-6) policy = new ResendPolicy(interval);
                float now = Time.time;
                Kept.Clear();
                foreach (ZDO zdo in __1)
                {
                    if (ReferenceEquals(zdo, null)) { Kept.Add(zdo!); continue; }
                    bool cosmetic = CosmeticPrefabs.Contains(zdo.GetPrefab());
                    float syncTime = 0f;
                    bool hasPrevious = cosmetic && syncTimes.TryGet(zdos, zdo.m_uid, out syncTime);
                    double age = hasPrevious ? now - syncTime : 0;
                    var decision = policy.Evaluate(cosmetic, cosmetic && zdo.IsOwner(), zdo.Type == ZDO.ObjectType.Prioritized,
                        cosmetic && forced != null && forced.Contains(zdo.m_uid), hasPrevious, age);
                    if (decision == ResendDecision.Allow) Kept.Add(zdo);
                }
                passes++;
                if (Kept.Count == __1.Count) { Kept.Clear(); return; }
                removedEntries += __1.Count - Kept.Count;
                __1.Clear();
                __1.AddRange(Kept);
                Kept.Clear();
            }
            catch (Exception exception)
            {
                cadenceFailed = true;
                Status = "failed_resend_interval";
                Kept.Clear();
                logger.LogWarning("Cosmetic resend interval disabled after a selection failure; vanilla cadence retained: " + exception.GetType().Name);
            }
        }

        private static void ResolvePrefabs()
        {
            ZNetScene current = ZNetScene.instance;
            if (current == null) return;
            if (scene != null && scene.TryGetTarget(out ZNetScene previous) && ReferenceEquals(previous, current)) return;
            scene = new WeakReference<ZNetScene>(current);
            CosmeticPrefabs.Clear();
            configuredNames = knownPrefabs = unknownPrefabs = 0;
            var unresolved = new List<string>();
            foreach (string entry in (cosmeticNames.Value ?? "").Split(','))
            {
                string name = entry.Trim();
                if (name.Length == 0) continue;
                configuredNames++;
                int hash = name.GetStableHashCode();
                if (current.GetPrefab(hash) == null) { unknownPrefabs++; unresolved.Add(name); continue; }
                if (CosmeticPrefabs.Add(hash)) knownPrefabs++;
            }
            // Configured names only, never scene or player data; an unknown name is a config typo or a game rename.
            logger.LogInfo("Cosmetic resend interval resolved " + knownPrefabs + " of " + configuredNames + " configured prefabs"
                + (unresolved.Count == 0 ? "." : "; unresolved: " + string.Join(",", unresolved.ToArray()) + "."));
        }

        // Part B. Runs after vanilla has written position, rotation and its own zero
        // velocity, so the value published here is the one the peer reads. Non-owners
        // consume it in vanilla ZSyncTransform.SyncPosition as world metres per second.
        private static void AfterOwnerSync(ZSyncTransform __instance)
        {
            if (!BirdVelocityActive || __instance == null || bodyField == null || viewField == null) return;
            try
            {
                int id = __instance.GetInstanceID();
                if (!BirdKinds.TryGetValue(id, out bool isBird))
                {
                    if (BirdKinds.Count >= BirdCapacity) { birdBoundedSkips++; return; }
                    isBird = !(bodyField.GetValue(__instance) is UnityEngine.Object rigidbody && rigidbody != null) &&
                        __instance.GetComponent<RandomFlyingBird>() != null;
                    BirdKinds[id] = isBird;
                }
                if (!isBird || !__instance.m_syncPosition) return;
                ZNetView? tracked = viewField.GetValue(__instance) as ZNetView;
                if (tracked == null || !tracked.IsValid()) return;
                ZDO zdo = tracked.GetZDO();
                if (zdo == null || !zdo.IsOwner()) return;
                Vector3 position = __instance.transform.position;
                float now = Time.time;
                if (!BirdMotions.TryGetValue(id, out BirdMotion motion))
                {
                    if (BirdMotions.Count >= BirdCapacity) { birdBoundedSkips++; return; }
                    BirdMotions[id] = new BirdMotion { LastPosition = position, LastMoveTime = now };
                    return;
                }
                Vector3 delta = position - motion.LastPosition;
                float elapsed = now - motion.LastMoveTime;
                Vector3 velocity;
                if (delta.sqrMagnitude > MovementEpsilonSquared)
                {
                    if (elapsed <= 0f)
                    {
                        motion.LastPosition = position;
                        motion.LastMoveTime = now;
                        BirdMotions[id] = motion;
                        return;
                    }
                    velocity = delta / elapsed;
                    motion.LastPosition = position;
                    motion.LastMoveTime = now;
                    // A respawn, a landing snap or a long frame makes the difference
                    // quotient meaningless; publish no extrapolation rather than a wrong one.
                    if (elapsed > StaleSeconds || velocity.sqrMagnitude > MaxSpeed * MaxSpeed)
                    {
                        birdResets++;
                        velocity = Vector3.zero;
                    }
                }
                else if (elapsed >= StillSeconds) velocity = Vector3.zero;
                else { BirdMotions[id] = motion; return; }
                if (motion.HasPublished && (velocity - motion.Published).sqrMagnitude < PublishEpsilonSquared)
                {
                    BirdMotions[id] = motion;
                    return;
                }
                zdo.Set(ZDOVars.s_velHash, velocity);
                motion.Published = velocity;
                motion.HasPublished = true;
                BirdMotions[id] = motion;
                birdPublications++;
            }
            catch (Exception exception)
            {
                birdFailed = true;
                Status = "failed_bird_velocity";
                logger.LogWarning("Bird velocity publication disabled after a failure: " + exception.GetType().Name);
            }
        }

        private static void AfterDisable(ZSyncTransform __instance)
        {
            if (__instance == null) return;
            int id = __instance.GetInstanceID();
            BirdKinds.Remove(id);
            BirdMotions.Remove(id);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("replication_cadence_status", Status));
            labels.Add(new TextValue("replication_resend_interval_enabled", CadenceActive ? "true" : "false"));
            labels.Add(new TextValue("replication_bird_velocity_enabled", BirdVelocityActive ? "true" : "false"));
            labels.Add(new TextValue("replication_cadence_scope",
                "owned_non_prioritized_non_forced_cosmetic_prefabs_only; deferral_is_delay_not_loss; wire_format_and_revisions_unchanged"));
            gauges.Add(new NumberValue("replication_resend_interval_seconds", policy?.IntervalSeconds ?? 0, "seconds"));
            gauges.Add(new NumberValue("replication_cosmetic_prefabs_configured", configuredNames, "prefabs"));
            gauges.Add(new NumberValue("replication_cosmetic_prefabs_known", knownPrefabs, "prefabs"));
            gauges.Add(new NumberValue("replication_cosmetic_prefabs_unknown", unknownPrefabs, "prefabs"));
            gauges.Add(new NumberValue("replication_sync_list_passes_total", passes, "passes"));
            gauges.Add(new NumberValue("replication_sync_list_bounded_skips_total", boundedSkips, "passes"));
            gauges.Add(new NumberValue("replication_deferred_entries_total", removedEntries, "objects"));
            ResendCounters counters = policy?.Counters ?? default;
            gauges.Add(new NumberValue("replication_considered_entries_total", counters.Considered, "objects"));
            gauges.Add(new NumberValue("replication_allowed_entries_total", counters.Allowed, "objects"));
            gauges.Add(new NumberValue("replication_deferred_decisions_total", counters.Deferred, "objects"));
            gauges.Add(new NumberValue("replication_first_send_entries_total", counters.FirstSends, "objects"));
            gauges.Add(new NumberValue("replication_forced_entries_total", counters.ForcedKept, "objects"));
            gauges.Add(new NumberValue("replication_prioritized_entries_total", counters.PrioritizedKept, "objects"));
            gauges.Add(new NumberValue("replication_foreign_owner_entries_total", counters.ForeignKept, "objects"));
            gauges.Add(new NumberValue("replication_invalid_age_entries_total", counters.InvalidAges, "objects"));
            gauges.Add(new NumberValue("replication_bird_velocity_publications_total", birdPublications, "writes"));
            gauges.Add(new NumberValue("replication_bird_velocity_resets_total", birdResets, "writes"));
            gauges.Add(new NumberValue("replication_bird_tracked_instances", BirdMotions.Count, "instances"));
            gauges.Add(new NumberValue("replication_bird_bounded_skips_total", birdBoundedSkips, "calls"));
        }

        internal static void Reset()
        {
            policy?.Reset();
            passes = boundedSkips = removedEntries = 0;
            birdPublications = birdResets = birdBoundedSkips = 0;
            Kept.Clear();
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Reset();
            BirdKinds.Clear();
            BirdMotions.Clear();
            CosmeticPrefabs.Clear();
            scene = null;
            cadencePatched = birdPatched = Installed = false;
            bodyField = null;
            viewField = null;
            ZdoPeerAccess.Clear();
            try { Patches.UnpatchSelf(); Status = "disabled"; }
            catch (Exception exception) { Status = "unpatch_failed:" + exception.GetType().Name; }
        }
    }
}
