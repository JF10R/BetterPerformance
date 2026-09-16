using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BetterPerformance.Core;
using BepInEx.Logging;
using HarmonyLib;

namespace BetterPerformance
{
    internal static class TimingHooks
    {
        private static readonly Dictionary<MethodBase, Metric> Metrics = new Dictionary<MethodBase, Metric>();
        internal static readonly List<TextValue> Availability = new List<TextValue>();
        internal static CaptureSession? Current;

        internal static void Install(Harmony harmony, ManualLogSource logger, bool enabled)
        {
            Add(harmony, logger, enabled, typeof(ZNet), "Update", Metric.NetworkUpdate, Type.EmptyTypes);
            // Loading stages are inclusive elapsed times. Parent/child and worker
            // durations overlap; never add them to infer exclusive CPU or wall time.
            Add(harmony, logger, enabled, typeof(ZNet), "RPC_PeerInfo", Metric.JoinPeerInfo,
                new[] { typeof(ZRpc), typeof(ZPackage) });
            Add(harmony, logger, enabled, typeof(WorldGenerator), "Initialize", Metric.WorldInitialize,
                new[] { typeof(World) });
            Add(harmony, logger, enabled, typeof(WorldGenerator), "Pregenerate", Metric.WorldPregenerate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(WorldGenerator), "FindLakes", Metric.WorldFindLakes, Type.EmptyTypes);
            Type? riverType = AccessTools.Inner(typeof(WorldGenerator), "River");
            if (riverType != null)
            {
                Type rivers = typeof(List<>).MakeGenericType(riverType);
                Add(harmony, logger, enabled, typeof(WorldGenerator), "PlaceRivers", Metric.WorldPlaceRivers, Type.EmptyTypes, rivers);
                Add(harmony, logger, enabled, typeof(WorldGenerator), "PlaceStreams", Metric.WorldPlaceStreams, new[] { typeof(bool) }, rivers);
            }
            else
            {
                Availability.Add(new TextValue("probe.WorldPlaceRivers", enabled ? "unavailable" : "disabled"));
                Availability.Add(new TextValue("probe.WorldPlaceStreams", enabled ? "unavailable" : "disabled"));
            }
            Add(harmony, logger, enabled, typeof(ZoneSystem), "CreateLocalZones", Metric.LocalZoneDemand,
                new[] { typeof(UnityEngine.Vector3) }, typeof(bool));
            Type? terrainType = AccessTools.Inner(typeof(HeightmapBuilder), "HMBuildData");
            if (terrainType != null)
            {
                Add(harmony, logger, enabled, typeof(HeightmapBuilder), "RequestTerrainSync", Metric.TerrainSyncWait,
                    new[] { typeof(UnityEngine.Vector3), typeof(int), typeof(float), typeof(bool), typeof(WorldGenerator) }, terrainType);
                Add(harmony, logger, enabled, typeof(HeightmapBuilder), "Build", Metric.TerrainBuildWorker, new[] { terrainType });
            }
            else
            {
                Availability.Add(new TextValue("probe.TerrainSyncWait", enabled ? "unavailable" : "disabled"));
                Availability.Add(new TextValue("probe.TerrainBuildWorker", enabled ? "unavailable" : "disabled"));
            }
            Add(harmony, logger, enabled, typeof(Minimap), "TryLoadMinimapTextureData", Metric.MapTextureCacheLoad,
                new[] { typeof(int) }, typeof(bool));
            Add(harmony, logger, enabled, typeof(Minimap), "GenerateWorldMap", Metric.WorldMapGenerate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(Minimap), "SetMapData", Metric.PlayerMapLoad, new[] { typeof(byte[]) });
            Add(harmony, logger, enabled, typeof(Game), "CollectResourcesCheck", Metric.SpawnResourceCheck, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZDOMan), "Update", Metric.ReplicationUpdate, new[] { typeof(float) });
            Add(harmony, logger, enabled, typeof(ZNetScene), "Update", Metric.SceneUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZoneSystem), "Update", Metric.ZoneUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZNetScene), "CreateObjects", Metric.ObjectCreate,
                new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
            Add(harmony, logger, enabled, typeof(ZNetScene), "RemoveObjects", Metric.ObjectRemove,
                new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
            Add(harmony, logger, enabled, typeof(ZNet), "SaveWorld", Metric.SaveWorldCall, new[] { typeof(bool) });
            Add(harmony, logger, enabled, typeof(ZNet), "SaveWorldThread", Metric.SaveWorker, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZDOMan), "PrepareSave", Metric.SavePrepare, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZNet), "UpdatePeers", Metric.NetworkPeers, new[] { typeof(float) });
            Add(harmony, logger, enabled, typeof(ZNet), "UpdateSave", Metric.SaveUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZRpc), "Update", Metric.RpcUpdate, new[] { typeof(float) }, typeof(ZRpc.ErrorCode));
            Add(harmony, logger, enabled, typeof(ZNetScene), "CreateObjectsSorted", Metric.ObjectCreateSorted,
                new[] { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() });
            Add(harmony, logger, enabled, typeof(ZNetScene), "CreateDistantObjects", Metric.DistantObjectCreate,
                new[] { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() });
            // Inclusive elapsed stages, intentionally nested. A character save can
            // execute inside an RPC; none of these probes changes save scheduling.
            Add(harmony, logger, enabled, typeof(Game), "SavePlayerProfile", Metric.CharacterSave,
                new[] { typeof(bool), typeof(bool) });
            Add(harmony, logger, enabled, typeof(Minimap), "GetMapData", Metric.MapSerialization,
                Type.EmptyTypes, typeof(byte[]));
            Add(harmony, logger, enabled, typeof(PlayerProfile), "SavePlayerToDisk", Metric.CharacterSaveToDisk,
                Type.EmptyTypes, typeof(bool));
            Add(harmony, logger, enabled, typeof(ZDOMan), "GetSaveClonePerChunk", Metric.SaveClone,
                Type.EmptyTypes, typeof(List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>>));
            Add(harmony, logger, enabled, typeof(ZRpc), "HandlePackage", Metric.RpcDispatch, new[] { typeof(ZPackage) });
            Add(harmony, logger, enabled, typeof(ZDOMan), "RPC_ZDOData", Metric.IncomingZdoData,
                new[] { typeof(ZRpc), typeof(ZPackage) });
            Add(harmony, logger, enabled, typeof(ZDOMan), "FindSectorObjects", Metric.SectorDiscovery,
                new[] { typeof(Vector2s), typeof(SimulationDistance), typeof(List<ZDO>), typeof(List<ZDO>) });
            // ZDOPeer is private in the game assembly; do not depend on publicized DLLs.
            Type? peerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
            if (peerType != null)
            {
                Add(harmony, logger, enabled, typeof(ZDOMan), "CreateSyncList", Metric.SyncListBuild,
                    new[] { peerType, typeof(List<ZDO>) });
                Add(harmony, logger, enabled, typeof(ZDOMan), "SendZDOs", Metric.SendZdos,
                    new[] { peerType, typeof(bool) }, typeof(bool));
                Add(harmony, logger, enabled, typeof(ZDOMan), "ClientSortSendZDOS", Metric.ClientReplicationSort,
                    new[] { typeof(List<ZDO>), peerType });
                Add(harmony, logger, enabled, typeof(ZDOMan), "ServerSortSendZDOS", Metric.ServerReplicationSort,
                    new[] { typeof(List<ZDO>), typeof(UnityEngine.Vector3), peerType });
            }
            else
            {
                Availability.Add(new TextValue("probe.SyncListBuild", enabled ? "unavailable" : "disabled"));
                Availability.Add(new TextValue("probe.SendZdos", enabled ? "unavailable" : "disabled"));
                Availability.Add(new TextValue("probe.ClientReplicationSort", enabled ? "unavailable" : "disabled"));
                Availability.Add(new TextValue("probe.ServerReplicationSort", enabled ? "unavailable" : "disabled"));
            }
            Add(harmony, logger, enabled, typeof(Pathfinding), "GetPath", Metric.PathQuery,
                AiTelemetry.PathArguments, typeof(bool));
            Add(harmony, logger, enabled, typeof(Pathfinding), "UpdatePathfinding", Metric.PathfindingUpdate, Type.EmptyTypes);
            Type? spawnData = AccessTools.Inner(typeof(SpawnSystem), "SpawnData");
            if (spawnData != null)
            {
                Add(harmony, logger, enabled, typeof(SpawnSystem), "UpdateSpawnList", Metric.SpawnListUpdate,
                    new[] { typeof(List<>).MakeGenericType(spawnData), typeof(DateTime), typeof(bool), typeof(string) });
                Add(harmony, logger, enabled, typeof(SpawnSystem), "Spawn", Metric.SpawnAttempt,
                    new[] { spawnData, typeof(UnityEngine.Vector3), typeof(bool) });
            }
            else
            {
                Availability.Add(new TextValue("probe.SpawnListUpdate", enabled ? "unavailable" : "disabled"));
                Availability.Add(new TextValue("probe.SpawnAttempt", enabled ? "unavailable" : "disabled"));
            }
            AiTelemetry.Install(harmony, logger, enabled);
            string characterStatus = "disabled";
            if (enabled)
            {
                try
                {
                    Type? updater = typeof(ZNet).Assembly.GetType("MonoUpdatersExtra");
                    Type? item = typeof(ZNet).Assembly.GetType("IMonoUpdater");
                    if (updater == null || item == null) characterStatus = "unavailable";
                    else
                    {
                        Type list = typeof(List<>).MakeGenericType(item);
                        var method = AccessTools.DeclaredMethod(updater, "CustomFixedUpdate", new[] { list, list, typeof(string), typeof(float) });
                        if (method == null || !method.IsStatic || method.ReturnType != typeof(void)) characterStatus = "unavailable";
                        else
                        {
                            harmony.Patch(method, prefix: new HarmonyMethod(typeof(TimingHooks), nameof(CharacterPrefix)),
                                finalizer: new HarmonyMethod(typeof(TimingHooks), nameof(CharacterFinalizer)));
                            characterStatus = "enabled";
                        }
                    }
                }
                catch (Exception exception) { characterStatus = "patch_failed"; logger.LogWarning("Character batch probe unavailable: " + exception.GetType().Name); }
            }
            Availability.Add(new TextValue("probe.CharacterFixedBatch", characterStatus));
            string readiness = "disabled";
            if (enabled)
            {
                try
                {
                    var method = AccessTools.DeclaredMethod(typeof(ZoneSystem), "IsActiveAreaLoaded", Type.EmptyTypes);
                    if (method == null || method.ReturnType != typeof(bool)) readiness = "unavailable";
                    else
                    {
                        harmony.Patch(method, postfix: new HarmonyMethod(typeof(TimingHooks), nameof(ReadinessPostfix)));
                        readiness = "enabled";
                    }
                }
                catch (Exception exception) { readiness = "patch_failed"; logger.LogWarning("Zone readiness probe unavailable: " + exception.GetType().Name); }
            }
            Availability.Add(new TextValue("probe.ZoneReadiness", readiness));
            InstallSimulation(harmony, logger, enabled);
        }

        // Late-update batch label supplied by the vanilla MonoUpdaters caller.
        internal const string HeightmapLateBatchName = "MonoUpdaters.LateUpdate.Heightmap";

        // Heightmap and every other IMonoUpdater implementor cannot be type-loaded by a
        // standalone CLR (default interface methods). Resolve them by name so offline
        // verification reports unavailable instead of failing the whole installer.
        private static Type? Resolve(string name)
        {
            try { return typeof(ZNet).Assembly.GetType(name, false); }
            catch { return null; }
        }

        private static void Unavailable(bool enabled, params Metric[] metrics)
        {
            foreach (Metric metric in metrics)
                Availability.Add(new TextValue("probe." + metric, enabled ? "unavailable" : "disabled"));
        }

        // Base-simulation probes: structural wear, terrain/heightmap rebuilds, slow
        // container ticks and zone/dungeon generation. Per-piece and per-instance frame
        // methods (WearNTear.UpdateWear, TerrainComp.Update) are deliberately not hooked.
        private static void InstallSimulation(Harmony harmony, ManualLogSource logger, bool enabled)
        {
            Add(harmony, logger, enabled, typeof(WearNTearUpdater), "UpdateWearNTear", Metric.WearBatch,
                new[] { typeof(float), typeof(float) });
            Add(harmony, logger, enabled, typeof(WearNTear), "UpdateSupport", Metric.WearSupportUpdate, Type.EmptyTypes);
            InstallHeightmapLateBatch(harmony, logger, enabled);
            Type? heightmap = Resolve("Heightmap");
            if (heightmap != null)
            {
                Add(harmony, logger, enabled, heightmap, "Regenerate", Metric.HeightmapRegenerate, Type.EmptyTypes);
                Add(harmony, logger, enabled, heightmap, "ApplyModifiers", Metric.HeightmapApplyModifiers, Type.EmptyTypes);
                Add(harmony, logger, enabled, heightmap, "RebuildCollisionMesh", Metric.HeightmapCollisionRebuild, Type.EmptyTypes);
                Add(harmony, logger, enabled, heightmap, "RebuildRenderMesh", Metric.HeightmapRenderRebuild, Type.EmptyTypes);
                Add(harmony, logger, enabled, typeof(TerrainComp), "ApplyToHeightmap", Metric.TerrainCompApply,
                    new[] { typeof(UnityEngine.Texture2D), typeof(List<float>), typeof(float[]), typeof(float[]), heightmap });
            }
            else
            {
                Unavailable(enabled, Metric.HeightmapRegenerate, Metric.HeightmapApplyModifiers,
                    Metric.HeightmapCollisionRebuild, Metric.HeightmapRenderRebuild, Metric.TerrainCompApply);
            }
            // SlowUpdater.UpdateLoop is a coroutine; timing its IEnumerator factory would
            // measure allocation, not the work the coroutine performs across frames.
            string slowLoop = "disabled";
            if (enabled)
            {
                try
                {
                    var method = AccessTools.DeclaredMethod(typeof(SlowUpdater), "UpdateLoop", Type.EmptyTypes);
                    slowLoop = method == null ? "unavailable"
                        : typeof(System.Collections.IEnumerator).IsAssignableFrom(method.ReturnType)
                            ? "unavailable_coroutine" : "unavailable_unexpected_shape";
                }
                catch (Exception exception) { slowLoop = "patch_failed"; logger.LogWarning("Slow update probe unavailable: " + exception.GetType().Name); }
            }
            Availability.Add(new TextValue("probe.SlowUpdateLoop", slowLoop));
            Add(harmony, logger, enabled, typeof(Plant), "SUpdate", Metric.PlantUpdate,
                new[] { typeof(float), typeof(Vector2s) });
            Add(harmony, logger, enabled, typeof(Smelter), "UpdateSmelter", Metric.SmelterUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(Fireplace), "UpdateFireplace", Metric.FireplaceUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(CookingStation), "UpdateCooking", Metric.CookingStationUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(Beehive), "UpdateBees", Metric.BeehiveUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(SapCollector), "UpdateTick", Metric.SapCollectorUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(Fermenter), "SlowUpdate", Metric.FermenterUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(Windmill), "Update", Metric.WindmillUpdate, Type.EmptyTypes);
            Add(harmony, logger, enabled, typeof(ZoneSystem), "SpawnLocation", Metric.LocationSpawn,
                new[] { typeof(ZoneSystem.ZoneLocation), typeof(int), typeof(UnityEngine.Vector3), typeof(UnityEngine.Quaternion),
                    typeof(ZoneSystem.SpawnMode), typeof(List<UnityEngine.GameObject>), typeof(bool) },
                typeof(UnityEngine.GameObject));
            Add(harmony, logger, enabled, typeof(ZoneSystem), "SpawnZone", Metric.ZoneSpawn,
                new[] { typeof(Vector2s), typeof(ZoneSystem.SpawnMode), typeof(UnityEngine.GameObject).MakeByRefType() }, typeof(bool));
            // ClearArea is a private nested type; do not depend on publicized DLLs.
            Type? clearArea = AccessTools.Inner(typeof(ZoneSystem), "ClearArea");
            if (clearArea != null && heightmap != null)
            {
                Type[] placement = { typeof(Vector2s), typeof(UnityEngine.Vector3), typeof(UnityEngine.Transform), heightmap,
                    typeof(List<>).MakeGenericType(clearArea), typeof(ZoneSystem.SpawnMode), typeof(List<UnityEngine.GameObject>) };
                Add(harmony, logger, enabled, typeof(ZoneSystem), "PlaceVegetation", Metric.VegetationPlace, placement);
                Add(harmony, logger, enabled, typeof(ZoneSystem), "PlaceLocations", Metric.ZonePlaceLocations, placement);
            }
            else
            {
                Unavailable(enabled, Metric.VegetationPlace, Metric.ZonePlaceLocations);
            }
            // Generate(SpawnMode) is the outer entry; it delegates to Generate(int, SpawnMode).
            Add(harmony, logger, enabled, typeof(DungeonGenerator), "Generate", Metric.DungeonGenerate,
                new[] { typeof(ZoneSystem.SpawnMode) });
            Add(harmony, logger, enabled, typeof(DungeonGenerator), "Spawn", Metric.DungeonSpawn, Type.EmptyTypes);
        }

        private static void InstallHeightmapLateBatch(Harmony harmony, ManualLogSource logger, bool enabled)
        {
            string status = "disabled";
            if (enabled)
            {
                try
                {
                    Type? updater = typeof(ZNet).Assembly.GetType("MonoUpdatersExtra");
                    Type? item = typeof(ZNet).Assembly.GetType("IMonoUpdater");
                    if (updater == null || item == null) status = "unavailable";
                    else
                    {
                        Type list = typeof(List<>).MakeGenericType(item);
                        var method = AccessTools.DeclaredMethod(updater, "CustomLateUpdate",
                            new[] { list, list, typeof(string), typeof(float) });
                        if (method == null || !method.IsStatic || method.ReturnType != typeof(void)) status = "unavailable";
                        else
                        {
                            harmony.Patch(method, prefix: new HarmonyMethod(typeof(TimingHooks), nameof(HeightmapLatePrefix)),
                                finalizer: new HarmonyMethod(typeof(TimingHooks), nameof(HeightmapLateFinalizer)));
                            status = "enabled";
                        }
                    }
                }
                catch (Exception exception) { status = "patch_failed"; logger.LogWarning("Heightmap batch probe unavailable: " + exception.GetType().Name); }
            }
            Availability.Add(new TextValue("probe.HeightmapLateBatch", status));
        }

        private static void Add(Harmony harmony, ManualLogSource logger, bool enabled, Type type,
            string name, Metric metric, Type[] arguments, Type? returnType = null)
        {
            string status = "disabled";
            if (enabled)
            {
                try
                {
                    var method = AccessTools.DeclaredMethod(type, name, arguments);
                    if (method == null || method.ReturnType != (returnType ?? typeof(void))) status = "unavailable";
                    else
                    {
                        Metrics[method] = metric;
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(TimingHooks), nameof(Prefix)),
                            finalizer: new HarmonyMethod(typeof(TimingHooks), nameof(Finalizer)));
                        status = "enabled";
                    }
                }
                catch (Exception exception) { status = "patch_failed"; logger.LogWarning("Probe " + metric + " unavailable: " + exception.GetType().Name); }
            }
            Availability.Add(new TextValue("probe." + metric, status));
        }

        internal struct TimingState
        {
            internal CaptureSession? Session;
            internal long Started;
        }

        private static void Prefix(out TimingState __state)
        {
            var session = System.Threading.Volatile.Read(ref Current);
            __state = new TimingState { Session = session, Started = session == null ? 0 : Stopwatch.GetTimestamp() };
        }

        private static void CharacterPrefix(string __2, out TimingState __state)
        {
            __state = default;
            if (__2 == "MonoUpdaters.FixedUpdate.Character") Prefix(out __state);
        }

        // Inclusive character dispatch (movement/grounding/etc.), not the physics solver.
        private static void CharacterFinalizer(TimingState __state, Exception? __exception)
        {
            if (__state.Session == null) return;
            try { __state.Session.Book.Record(Metric.CharacterFixedBatch,
                (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency, __exception != null); }
            catch { __state.Session.RecordProbeFailure(); }
        }

        // A void finalizer observes failed calls without replacing or suppressing their exception.
        private static void Finalizer(MethodBase __originalMethod, TimingState __state, Exception? __exception)
        {
            if (__state.Session == null) return;
            try
            {
                if (Metrics.TryGetValue(__originalMethod, out var metric))
                    __state.Session.Book.Record(metric, (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency,
                        __exception != null);
            }
            catch { __state.Session.RecordProbeFailure(); }
        }

        private static void HeightmapLatePrefix(string __2, out TimingState __state)
        {
            __state = default;
            if (__2 == HeightmapLateBatchName) Prefix(out __state);
        }

        // Inclusive heightmap late dispatch (mesh/collision rebuild work reached from it),
        // not exclusive terrain CPU time.
        private static void HeightmapLateFinalizer(TimingState __state, Exception? __exception)
        {
            if (__state.Session == null) return;
            try { __state.Session.Book.Record(Metric.HeightmapLateBatch,
                (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency, __exception != null); }
            catch { __state.Session.RecordProbeFailure(); }
        }

        private static void ReadinessPostfix(bool __result)
        {
            var session = System.Threading.Volatile.Read(ref Current);
            if (session != null) session.RecordReadiness(__result);
        }
    }
}
