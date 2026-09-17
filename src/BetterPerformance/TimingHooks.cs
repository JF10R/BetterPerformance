using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            InstallBatch(harmony, logger, enabled, "CustomFixedUpdate", new[] { typeof(string), typeof(float) },
                FixedBatches, nameof(FixedBatchPrefix), "Fixed-update batch probe");
            InstallBatch(harmony, logger, enabled, "CustomUpdate", new[] { typeof(string), typeof(float), typeof(float) },
                UpdateBatches, nameof(UpdateBatchPrefix), "Update batch probe");
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
            InstallGameplay(harmony, logger, enabled);
        }

        // Late-update batch label supplied by the vanilla MonoUpdaters caller.
        internal const string HeightmapLateBatchName = "MonoUpdaters.LateUpdate.Heightmap";

        // Profiler-scope labels the vanilla MonoUpdaters callers pass. One patch per
        // dispatch method selects the metric from the label, so a new batch costs a
        // dictionary entry instead of another prefix/finalizer pair.
        internal static readonly Dictionary<string, Metric> FixedBatches = new Dictionary<string, Metric>
        {
            { "MonoUpdaters.FixedUpdate.Character", Metric.CharacterFixedBatch },
            { "MonoUpdaters.FixedUpdate.Floating", Metric.FloatingBatch },
            { "MonoUpdaters.FixedUpdate.Ship", Metric.ShipBatch },
            { "MonoUpdaters.FixedUpdate.ZSyncTransform", Metric.ZSyncTransformBatch },
            { "MonoUpdaters.FixedUpdate.ZSyncAnimation", Metric.ZSyncAnimationBatch }
        };

        internal static readonly Dictionary<string, Metric> UpdateBatches = new Dictionary<string, Metric>
        {
            { "MonoUpdaters.Update.CraftingStation", Metric.CraftingStationBatch },
            { "MonoUpdaters.Update.ZSFX", Metric.SfxBatch },
            { "MonoUpdaters.Update.InstanceRenderer", Metric.InstanceRendererBatch },
            { "MonoUpdaters.Update.Smoke", Metric.SmokeBatch }
        };

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

        // Gameplay-loop probes: inventory/container interaction, building, map, vehicles,
        // resource gathering, damage and the single-instance player/HUD frame methods.
        // Every game type is resolved by name: Player, Character, Humanoid and Ship
        // implement IMonoUpdater and cannot be type-loaded by a standalone CLR, so a
        // hard typeof would abort the whole installer during offline verification.
        private static void InstallGameplay(Harmony harmony, ManualLogSource logger, bool enabled)
        {
            Type? gui = Resolve("InventoryGui"), container = Resolve("Container"), inventory = Resolve("Inventory");
            Type? player = Resolve("Player"), character = Resolve("Character"), humanoid = Resolve("Humanoid");
            Type? hud = Resolve("Hud"), minimap = Resolve("Minimap"), mapMode = Resolve("Minimap+MapMode");
            Type? ship = Resolve("Ship"), vagon = Resolve("Vagon");
            Type? hit = Resolve("HitData"), item = Resolve("ItemDrop+ItemData"), itemDrop = Resolve("ItemDrop");
            Type? modifier = Resolve("HitData+DamageModifier");
            Type gameObject = typeof(UnityEngine.GameObject), vector = typeof(UnityEngine.Vector3);

            // Inventory and chests.
            AddResolved(harmony, logger, enabled, gui, "Update", Metric.InventoryGuiUpdate, Empty);
            AddResolved(harmony, logger, enabled, gui, "UpdateInventory", Metric.InventoryGridUpdate, new[] { player });
            AddResolved(harmony, logger, enabled, gui, "UpdateContainer", Metric.ContainerGridUpdate, new[] { player });
            AddResolved(harmony, logger, enabled, gui, "Show", Metric.InventoryGuiShow, new[] { container, typeof(int) });
            AddResolved(harmony, logger, enabled, container, "Interact", Metric.ContainerInteract,
                new[] { humanoid, typeof(bool), typeof(bool) }, typeof(bool));
            AddResolved(harmony, logger, enabled, container, "OnContainerChanged", Metric.ContainerChanged, Empty);
            // InvokeRepeating("CheckForChanges", 0, 1): once per second per loaded container,
            // not per frame, and it carries the inventory deserialization on change.
            AddResolved(harmony, logger, enabled, container, "CheckForChanges", Metric.ContainerCheckForChanges, Empty);
            AddResolved(harmony, logger, enabled, inventory, "AddItem", Metric.InventoryAddItem, new[] { item }, typeof(bool));
            AddResolved(harmony, logger, enabled, inventory, "MoveItemToThis", Metric.InventoryMoveItem,
                new[] { inventory, item });

            // Building.
            AddResolved(harmony, logger, enabled, player, "UpdatePlacementGhost", Metric.PlacementGhostUpdate, new[] { typeof(bool) });
            AddResolved(harmony, logger, enabled, player, "UpdatePlacement", Metric.PlacementUpdate,
                new[] { typeof(bool), typeof(float) });
            AddResolved(harmony, logger, enabled, player, "TryPlacePiece", Metric.PiecePlace, new[] { Resolve("Piece") }, typeof(bool));
            AddResolved(harmony, logger, enabled, hud, "UpdateBuild", Metric.BuildGuiUpdate, new[] { player, typeof(bool) });
            // The two untimed event branches of UpdatePlacement, and the piece-button rebuild
            // reached from it through Hud.TogglePieceSelection. All three are event-rate.
            AddResolved(harmony, logger, enabled, player, "RemovePiece", Metric.PieceRemove, Empty, typeof(bool));
            AddResolved(harmony, logger, enabled, player, "CopyPiece", Metric.PieceCopy, Empty, typeof(bool));
            AddResolved(harmony, logger, enabled, Resolve("BuildUi"), "OpenBuildMenu", Metric.BuildMenuOpen, Empty);

            // Map.
            AddResolved(harmony, logger, enabled, minimap, "Update", Metric.MinimapUpdate, Empty);
            AddResolved(harmony, logger, enabled, minimap, "UpdateExplore", Metric.MinimapExploreUpdate,
                new[] { typeof(float), player });
            AddResolved(harmony, logger, enabled, minimap, "UpdateMap", Metric.MinimapLargeMapUpdate,
                new[] { player, typeof(float), typeof(bool) });
            AddResolved(harmony, logger, enabled, minimap, "SetMapMode", Metric.MinimapSetMapMode, new[] { mapMode });

            // Vehicles. Vagon has no FixedUpdate or CustomFixedUpdate in this build.
            AddResolved(harmony, logger, enabled, ship, "CustomFixedUpdate", Metric.ShipFixedUpdate, new[] { typeof(float) });
            AddResolved(harmony, logger, enabled, vagon, "Update", Metric.VagonUpdate, Empty);
            AddResolved(harmony, logger, enabled, vagon, "AttachTo", Metric.VagonAttach, new[] { gameObject });
            AddResolved(harmony, logger, enabled, vagon, "Detach", Metric.VagonDetach, Empty);

            // Resource gathering and combat.
            AddResolved(harmony, logger, enabled, Resolve("TreeBase"), "RPC_Damage", Metric.TreeDamage, new[] { typeof(long), hit });
            AddResolved(harmony, logger, enabled, Resolve("TreeBase"), "SpawnLog", Metric.TreeSpawnLog, new[] { vector });
            AddResolved(harmony, logger, enabled, Resolve("TreeLog"), "RPC_Damage", Metric.TreeLogDamage, new[] { typeof(long), hit });
            AddResolved(harmony, logger, enabled, Resolve("TreeLog"), "Destroy", Metric.TreeLogDestroy, new[] { hit, typeof(bool) });
            AddResolved(harmony, logger, enabled, Resolve("MineRock5"), "RPC_Damage", Metric.MineRockDamage,
                new[] { typeof(long), hit, typeof(int) });
            AddResolved(harmony, logger, enabled, Resolve("MineRock5"), "DamageArea", Metric.MineRockDamageArea,
                new[] { typeof(int), hit }, typeof(bool));
            AddResolved(harmony, logger, enabled, Resolve("Destructible"), "RPC_Damage", Metric.DestructibleDamage,
                new[] { typeof(long), hit });
            AddResolved(harmony, logger, enabled, Resolve("Destructible"), "Destroy", Metric.DestructibleDestroy, new[] { hit });
            AddResolved(harmony, logger, enabled, Resolve("WearNTear"), "RPC_Damage", Metric.WearDamage, new[] { typeof(long), hit });
            AddResolved(harmony, logger, enabled, character, "RPC_Damage", Metric.CharacterDamage, new[] { typeof(long), hit });
            AddResolved(harmony, logger, enabled, character, "ApplyDamage", Metric.CharacterApplyDamage,
                new[] { hit, typeof(bool), typeof(bool), modifier });
            // Attack.Start takes a Rigidbody, which this plugin does not reference. The name
            // is unique on the type, so the overload is pinned by arity and return type.
            AddByName(harmony, logger, enabled, Resolve("Attack"), "Start", Metric.AttackStart, 9, typeof(bool),
                humanoid, Resolve("ZSyncAnimation"), Resolve("CharacterAnimEvent"), Resolve("VisEquipment"), item);
            AddResolved(harmony, logger, enabled, Resolve("Piece"), "DropResources", Metric.PieceDropResources, new[] { hit });
            AddResolved(harmony, logger, enabled, Resolve("DropOnDestroyed"), "OnDestroyed", Metric.DropTableDrop, Empty);

            // Stations.
            AddResolved(harmony, logger, enabled, Resolve("Smelter"), "Spawn", Metric.SmelterSpawn,
                new[] { typeof(string), typeof(int) });

            // Items. Both run from InvokeRepeating on a 10 second period per dropped item.
            AddResolved(harmony, logger, enabled, itemDrop, "SlowUpdate", Metric.ItemDropSlowUpdate, Empty);
            AddResolved(harmony, logger, enabled, itemDrop, "AutoStackItems", Metric.ItemAutoStack, Empty);
            AddResolved(harmony, logger, enabled, Resolve("Pickable"), "Interact", Metric.PickableInteract,
                new[] { humanoid, typeof(bool), typeof(bool) }, typeof(bool));

            // Player and per-frame singletons.
            AddResolved(harmony, logger, enabled, player, "Update", Metric.PlayerUpdate, Empty);
            AddResolved(harmony, logger, enabled, player, "FixedUpdate", Metric.PlayerFixedUpdate, Empty);
            AddResolved(harmony, logger, enabled, hud, "Update", Metric.HudUpdate, Empty);
            // Clutter attribution: the ring sweep that admits patches, and the one patch it
            // generates. Their sum is what a heavy ClutterLateUpdate frame is made of.
            Type? clutter = Resolve("ClutterSystem");
            AddResolved(harmony, logger, enabled, clutter, "LateUpdate", Metric.ClutterLateUpdate, Empty);
            AddResolved(harmony, logger, enabled, clutter, "GeneratePatches", Metric.ClutterGeneratePatches,
                new[] { typeof(bool), vector });
            // GenerateVegPatch returns ClutterSystem.PatchData, a private nested type; do not
            // depend on publicized DLLs.
            Type? patchData = clutter == null ? null : AccessTools.Inner(clutter, "PatchData");
            if (patchData == null) Unavailable(enabled, Metric.ClutterGenerateVegPatch);
            else AddResolved(harmony, logger, enabled, clutter, "GenerateVegPatch", Metric.ClutterGenerateVegPatch,
                new[] { typeof(UnityEngine.Vector2Int), typeof(float) }, patchData);
            AddResolved(harmony, logger, enabled, Resolve("WaterVolume"), "StaticUpdate", Metric.WaterStaticUpdate, Empty);
        }

        private static readonly Type?[] Empty = new Type?[0];

        // A null owner or parameter type means the standalone CLR could not load it;
        // report unavailable rather than guessing a signature.
        private static void AddResolved(Harmony harmony, ManualLogSource logger, bool enabled, Type? owner,
            string name, Metric metric, Type?[] arguments, Type? returnType = null)
        {
            if (owner == null || Array.IndexOf(arguments, null) >= 0) { Unavailable(enabled, metric); return; }
            Type[] resolved = new Type[arguments.Length];
            for (int i = 0; i < arguments.Length; i++) resolved[i] = arguments[i]!;
            Add(harmony, logger, enabled, owner, name, metric, resolved, returnType);
        }

        // Pins a uniquely named overload by arity and return type when one of its parameter
        // types cannot be referenced from this assembly. The required types still gate it.
        private static void AddByName(Harmony harmony, ManualLogSource logger, bool enabled, Type? owner,
            string name, Metric metric, int parameters, Type? returnType, params Type?[] required)
        {
            if (owner == null || Array.IndexOf(required, null) >= 0) { Unavailable(enabled, metric); return; }
            string status = "disabled";
            if (enabled)
            {
                try
                {
                    var matches = owner.GetMethods(AccessTools.all).Where(m => m.Name == name && m.DeclaringType == owner).ToArray();
                    var method = matches.Length == 1 ? matches[0] : null;
                    if (method == null || method.GetParameters().Length != parameters ||
                        method.ReturnType != (returnType ?? typeof(void))) status = "unavailable";
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

        // One prefix/finalizer pair per MonoUpdaters dispatch method; the profiler-scope
        // string selects the metric.
        private static void InstallBatch(Harmony harmony, ManualLogSource logger, bool enabled, string name,
            Type[] trailing, Dictionary<string, Metric> names, string prefix, string label)
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
                        var arguments = new Type[2 + trailing.Length];
                        arguments[0] = list;
                        arguments[1] = list;
                        Array.Copy(trailing, 0, arguments, 2, trailing.Length);
                        var method = AccessTools.DeclaredMethod(updater, name, arguments);
                        if (method == null || !method.IsStatic || method.ReturnType != typeof(void)) status = "unavailable";
                        else
                        {
                            harmony.Patch(method, prefix: new HarmonyMethod(typeof(TimingHooks), prefix),
                                finalizer: new HarmonyMethod(typeof(TimingHooks), nameof(BatchFinalizer)));
                            status = "enabled";
                        }
                    }
                }
                catch (Exception exception) { status = "patch_failed"; logger.LogWarning(label + " unavailable: " + exception.GetType().Name); }
            }
            foreach (Metric metric in names.Values) Availability.Add(new TextValue("probe." + metric, status));
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
            // Only the batch probes use this; the per-method finalizer reads the registry.
            internal Metric Metric;
        }

        private static void Prefix(out TimingState __state)
        {
            var session = System.Threading.Volatile.Read(ref Current);
            __state = new TimingState { Session = session, Started = session == null ? 0 : Stopwatch.GetTimestamp() };
        }

        private static void FixedBatchPrefix(string __2, out TimingState __state)
        {
            __state = default;
            if (__2 != null && FixedBatches.TryGetValue(__2, out Metric metric))
            { Prefix(out __state); __state.Metric = metric; }
        }

        private static void UpdateBatchPrefix(string __2, out TimingState __state)
        {
            __state = default;
            if (__2 != null && UpdateBatches.TryGetValue(__2, out Metric metric))
            { Prefix(out __state); __state.Metric = metric; }
        }

        // Inclusive dispatch of one MonoUpdaters group (character movement/grounding, ship
        // buoyancy, transform sync and so on), not the physics solver and not exclusive CPU.
        private static void BatchFinalizer(TimingState __state, Exception? __exception)
        {
            if (__state.Session == null) return;
            try { __state.Session.Book.Record(__state.Metric,
                (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency, __exception != null); }
            catch { __state.Session.RecordProbeFailure(); }
        }

        // Build-mode stall bucket. The histogram already counts stalls over 50 ms; the
        // build-menu hypothesis needs the 12-42 ms band, which sits below that bound.
        // Drained by the gameplay counters as placement_update_over_10ms.
        private static long placementUpdateOver10Ms;
        internal static long DrainPlacementUpdateOver10Ms() =>
            System.Threading.Interlocked.Exchange(ref placementUpdateOver10Ms, 0);

        // A void finalizer observes failed calls without replacing or suppressing their exception.
        private static void Finalizer(MethodBase __originalMethod, TimingState __state, Exception? __exception)
        {
            if (__state.Session == null) return;
            try
            {
                if (Metrics.TryGetValue(__originalMethod, out var metric))
                {
                    double elapsed = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                    __state.Session.Book.Record(metric, elapsed, __exception != null);
                    // One enum comparison per timed call. UpdatePlacement is local-player only,
                    // so this counter is written from the main thread and nowhere else.
                    if (metric == Metric.PlacementUpdate && elapsed > 10.0) placementUpdateOver10Ms++;
                }
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
