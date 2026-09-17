using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Counts and sizes for the gameplay surfaces a play session actually touches:
    // chests, inventories, stations, building, gathering, combat, the map, vehicles
    // and item drops. Nothing here times anything; elapsed time for the same call
    // sites belongs to the TimingHooks metrics. Every hook is a count-only prefix or
    // postfix that cannot skip or replace a native call, each probe is installed
    // independently so one changed signature degrades only its own counter, and the
    // exported state is a fixed set of scalars: no per-object records, no names.
    internal static class GameplayTelemetry
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".GameplayTelemetry");
        private static readonly List<string> Missing = new List<string>();
        private static int ownerThread, probesInstalled, probesAttempted;

        private static AccessTools.FieldRef<InventoryGui, Container>? shownContainer;
        private static AccessTools.FieldRef<Minimap, Minimap.MapMode>? mapMode;
        private static AccessTools.FieldRef<Humanoid, Inventory>? humanoidInventory;
        private static Func<object, int>? placementGhost;
        private static Func<object, bool>? attachedToShip;
        private static Func<Smelter, int>? queueSize, processedQueueSize;
        private static Func<object, int>? patchObjectCount, timedOutPatchCount;
        private static MemberCount shipInstances, vagonInstances, sfxInstances;

        // Container and inventory.
        private static long containerRequestsReceived, containerConflicts, containerGranted, containerRejectedInUse;
        private static long containerChanges, containerGuiFrames, inventoryGuiFrames, inventoryMoves, inventoryStacks;
        private static int inventoryItemsMax;
        // Stations.
        private static long smelterUpdates, smelterCatchupSum, smelterSpawns, fireplaceFuelAdds, cookingSpawns, beehiveExtracts;
        private static int smelterCatchupMax;
        // Building.
        private static long placementGhostCalls, placementGhostFrames, piecesPlaced, piecesRemoved;
        private static long snapPiecesScanned, snapPointsEnumerated, ghostClippingTests;
        // Clutter.
        private static long clutterPatchesGenerated, clutterRebuildAllFrames, clutterGroundQueries;
        private static long clutterObjectsInstantiated, clutterHeightmapNotReadyFrames, clutterPatchesTimedOut;
        // Gathering and combat.
        private static long treeDamageRpcs, treeLogsSpawned, rockDamageRpcs, rockAreaDestroys;
        private static long destructibleDestroys, attacksStarted, hitsDealt, dropEvents, dropsSpawned;
        // Map.
        private static long exploreUpdates, exploreScans, fogApplies, fogPixels, largeMapFrames;
        // Vehicles and frames.
        private static long observedFrames, onShipFrames;
        private static int shipMax, vagonMax, sfxMax;
        // Items.
        private static long autoStacks, slowUpdates;
        // Health.
        private static long otherThreadSkips, probeFailures;

        [ThreadStatic] private static int exploreDepth;
        [ThreadStatic] private static bool exploreChanged;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; set; }
        internal static string Status { get; private set; } = "disabled";

        // A static collection read through its Count property. The declaring types are
        // resolved by name because some of them implement a Unity interface that a
        // standalone CLR cannot type-load.
        private struct MemberCount
        {
            internal PropertyInfo? Property;
            internal FieldInfo? Field;
            internal PropertyInfo? Count;
            internal int Read()
            {
                object? collection = Property != null ? Property.GetValue(null, null) : Field?.GetValue(null);
                return collection == null || Count == null ? -1 : (int)Count.GetValue(collection, null)!;
            }
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            if (!config.Bind("Diagnostics", "GameplayCountersEnabled", true,
                "Count gameplay events and read a few bounded sizes: chest and inventory traffic, station " +
                "catch-up, building, gathering, combat, map exploration, vehicles and item drops. Counting only; " +
                "elapsed time for the same call sites stays with the timing metrics. Requires restart.").Value)
            { Status = "disabled"; return; }
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            Missing.Clear();
            probesInstalled = probesAttempted = 0;

            Probe("container_open", () =>
            {
                Patch("Container", "RPC_RequestOpen", new[] { typeof(long), typeof(long) }, typeof(void), nameof(BeforeRequestOpen), null);
                Patch("Container", "RPC_OpenResponse", new[] { typeof(long), typeof(bool) }, typeof(void), nameof(BeforeOpenResponse), null);
            });
            Probe("container_changed", () =>
                Patch("Container", "OnContainerChanged", Type.EmptyTypes, typeof(void), nameof(OnContainerChanged), null));
            Probe("inventory_move", () =>
                Patch("Inventory", "MoveItemToThis", new[] { Game("Inventory"), Game("ItemDrop+ItemData") }, typeof(void), nameof(OnInventoryMove), null));
            Probe("inventory_add", () =>
                Patch("Inventory", "AddItem", new[] { Game("ItemDrop+ItemData") }, typeof(bool), nameof(OnInventoryAdd), null));
            Probe("inventory_size", () => humanoidInventory = AccessTools.FieldRefAccess<Humanoid, Inventory>("m_inventory"));

            Probe("smelter", () =>
            {
                queueSize = Accessor<Smelter>("GetQueueSize");
                processedQueueSize = Accessor<Smelter>("GetProcessedQueueSize");
                Patch("Smelter", "UpdateSmelter", Type.EmptyTypes, typeof(void), nameof(BeforeSmelter), nameof(AfterSmelter));
            });
            Probe("smelter_spawn", () =>
                Patch("Smelter", "Spawn", new[] { typeof(string), typeof(int) }, typeof(void), nameof(OnSmelterSpawn), null));
            Probe("fireplace_fuel", () =>
                Patch("Fireplace", "RPC_AddFuel", new[] { typeof(long) }, typeof(void), nameof(OnFireplaceFuel), null));
            Probe("cooking_spawn", () =>
                Patch("CookingStation", "SpawnItem", new[] { typeof(string), typeof(int), typeof(Vector3), typeof(bool) },
                    typeof(void), nameof(OnCookingSpawn), null));
            Probe("beehive_extract", () =>
                Patch("Beehive", "RPC_Extract", new[] { typeof(long) }, typeof(void), nameof(OnBeehiveExtract), null));

            Probe("placement_ghost", () =>
            {
                placementGhost = GhostReader();
                Patch("Player", "UpdatePlacementGhost", new[] { typeof(bool) }, typeof(void), null, nameof(AfterPlacementGhost));
            });
            Probe("piece_place", () =>
                Patch("Player", "PlacePiece", new[] { Game("Piece"), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(bool) },
                    typeof(void), nameof(OnPiecePlaced), null));
            Probe("piece_remove", () =>
                Patch("Player", "RemovePiece", Type.EmptyTypes, typeof(bool), null, nameof(AfterPieceRemoved)));
            // Snap work per ghost refresh. The lists are the caller's reused buffers, so the
            // counters take their growth across the call, never their absolute length.
            Probe("snap_points", () =>
                PatchStatic("Piece", "GetSnapPoints",
                    new[] { typeof(Vector3), typeof(float), typeof(List<Transform>), GameList("Piece") },
                    typeof(void), nameof(BeforeSnapPoints), nameof(AfterSnapPoints)));
            Probe("ghost_clipping", () =>
                Patch("Player", "TestGhostClipping", new[] { typeof(GameObject), typeof(float) },
                    typeof(bool), null, nameof(AfterGhostClipping)));

            // Clutter. Attribution for a heavy ClutterLateUpdate frame: whether it generated
            // one patch or many, how many ground raycasts that patch drew, and how many
            // prefabs it instantiated.
            Probe("clutter_patch", () =>
            {
                patchObjectCount = CountReader(Inner("ClutterSystem", "PatchData"), "m_objects");
                Patch("ClutterSystem", "GenerateVegPatch", new[] { typeof(Vector2Int), typeof(float) },
                    Inner("ClutterSystem", "PatchData"), null, nameof(AfterGenerateVegPatch));
            });
            Probe("clutter_rebuild_all", () =>
                Patch("ClutterSystem", "UpdateGrass", new[] { typeof(float), typeof(bool), typeof(Vector3) },
                    typeof(void), nameof(BeforeUpdateGrass), null));
            // GetGroundInfo returns four values through out parameters whose types a
            // standalone CLR cannot load, so the overload is matched on shape instead.
            Probe("clutter_ground_query", () =>
                PatchByShape("ClutterSystem", "GetGroundInfo", 5, typeof(bool), null, nameof(AfterGroundInfo)));
            Probe("clutter_heightmap_ready", () =>
                Patch("ClutterSystem", "IsHeightmapReady", Type.EmptyTypes, typeof(bool), null, nameof(AfterHeightmapReady)));
            Probe("clutter_timeout", () =>
            {
                timedOutPatchCount = CountReader(Game("ClutterSystem"), "m_tempToRemovePair");
                Patch("ClutterSystem", "TimeoutPatches", new[] { typeof(float) }, typeof(void), null, nameof(AfterTimeoutPatches));
            });

            Probe("tree_damage", () =>
                Patch("TreeBase", "RPC_Damage", new[] { typeof(long), Game("HitData") }, typeof(void), nameof(OnTreeDamage), null));
            Probe("tree_log_spawn", () =>
                Patch("TreeBase", "SpawnLog", new[] { typeof(Vector3) }, typeof(void), nameof(OnTreeLogSpawn), null));
            Probe("rock_damage", () =>
                Patch("MineRock5", "RPC_Damage", new[] { typeof(long), Game("HitData"), typeof(int) }, typeof(void), nameof(OnRockDamage), null));
            Probe("rock_area", () =>
                Patch("MineRock5", "DamageArea", new[] { typeof(int), Game("HitData") }, typeof(bool), null, nameof(AfterRockArea)));
            Probe("destructible_destroy", () =>
                Patch("Destructible", "Destroy", new[] { Game("HitData") }, typeof(void), nameof(OnDestructibleDestroy), null));
            Probe("attack_start", () => PatchByShape("Attack", "Start", 9, typeof(bool), null, nameof(AfterAttackStart)));
            Probe("character_damage", () =>
                Patch("Character", "RPC_Damage", new[] { typeof(long), Game("HitData") }, typeof(void), nameof(OnCharacterDamage), null));
            Probe("drop_on_destroyed", () =>
                Patch("DropOnDestroyed", "OnDestroyed", Type.EmptyTypes, typeof(void), nameof(OnDropEvent), null));
            Probe("drop_table", () =>
                Patch("DropTable", "GetDropList", Type.EmptyTypes, typeof(List<GameObject>), null, nameof(AfterDropList)));

            Probe("minimap_explore", () =>
            {
                mapMode = AccessTools.FieldRefAccess<Minimap, Minimap.MapMode>("m_mode");
                Patch("Minimap", "UpdateExplore", new[] { typeof(float), Game("Player") }, typeof(void), nameof(OnExploreUpdate), null);
                Patch("Minimap", "Explore", new[] { typeof(Vector3), typeof(float) }, typeof(void), nameof(BeforeExplore), nameof(AfterExplore));
                Patch("Minimap", "Explore", new[] { typeof(int), typeof(int) }, typeof(bool), null, nameof(AfterExplorePixel));
            });

            Probe("item_autostack", () =>
                Patch("ItemDrop", "AutoStackItems", Type.EmptyTypes, typeof(void), nameof(OnAutoStack), null));
            Probe("item_slow_update", () =>
                Patch("ItemDrop", "SlowUpdate", Type.EmptyTypes, typeof(void), nameof(OnSlowUpdate), null));

            // One probe per list: resolving any of these types can fail on its own, and a
            // refusal must cost only its own occupancy gauge.
            Probe("ship_instances", () => shipInstances = Collection("Ship", "Instances"));
            Probe("zsfx_instances", () => sfxInstances = Collection("ZSFX", "Instances"));
            Probe("vagon_instances", () => vagonInstances = Collection("Vagon", "m_instances"));
            Probe("frame_observer", () =>
            {
                shownContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
                attachedToShip = ShipAttachmentReader();
                Patch("Hud", "Update", Type.EmptyTypes, typeof(void), null, nameof(AfterHudUpdate));
            });

            Installed = probesInstalled > 0;
            Enabled = Installed;
            Status = probesInstalled == 0 ? "unavailable" : Missing.Count == 0 ? "installed" : "partial";
            if (Missing.Count > 0)
                logger.LogWarning("Gameplay counters partially unavailable (" + Missing.Count + "/" + probesAttempted +
                    " probes): " + string.Join(",", Missing.ToArray()));
            if (probesInstalled == 0) Release();
        }

        private static void Probe(string name, Action install)
        {
            probesAttempted++;
            try { install(); probesInstalled++; }
            catch (Exception exception)
            {
                if (Missing.Count < 40) Missing.Add(name + ":" + exception.GetType().Name);
            }
        }

        private static Type Game(string name) => typeof(ZNet).Assembly.GetType(name, false)
            ?? throw new InvalidOperationException("Game type is unavailable: " + name);

        private static Type GameList(string element) => typeof(List<>).MakeGenericType(Game(element));

        // A private nested game type; do not depend on publicized DLLs.
        private static Type Inner(string owner, string nested) => AccessTools.Inner(Game(owner), nested)
            ?? throw new InvalidOperationException("Nested game type is unavailable: " + owner + "+" + nested);

        // Reads the Count of a collection held in a game field. The collection is never
        // enumerated, indexed or retained, and its element type is never resolved.
        private static Func<object, int> CountReader(Type owner, string fieldName)
        {
            FieldInfo field = AccessTools.Field(owner, fieldName)
                ?? throw new InvalidOperationException("Missing field: " + owner.Name + "." + fieldName);
            PropertyInfo? count = field.FieldType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (count == null || count.PropertyType != typeof(int))
                throw new InvalidOperationException("Field has no int Count: " + owner.Name + "." + fieldName);
            return instance =>
            {
                object? collection = field.GetValue(instance);
                return collection == null ? 0 : (int)count.GetValue(collection, null)!;
            };
        }

        // List<T> implements the non-generic ICollection, so a buffer passed as object is
        // measured without reflection and without naming its element type.
        private static int Size(object? collection) =>
            collection is System.Collections.ICollection list ? list.Count : -1;

        private static void Patch(string typeName, string name, Type[] arguments, Type result, string? prefix, string? postfix)
        {
            MethodInfo? method = AccessTools.DeclaredMethod(Game(typeName), name, arguments);
            if (method == null || method.IsStatic || method.ReturnType != result)
                throw new InvalidOperationException("Unsupported gameplay signature: " + typeName + "." + name);
            Patches.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(GameplayTelemetry), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(GameplayTelemetry), postfix));
        }

        // Same contract as Patch, for a static game method. Kept separate so the instance
        // check on every other hook stays exact.
        private static void PatchStatic(string typeName, string name, Type[] arguments, Type result, string? prefix, string? postfix)
        {
            MethodInfo? method = AccessTools.DeclaredMethod(Game(typeName), name, arguments);
            if (method == null || !method.IsStatic || method.ReturnType != result)
                throw new InvalidOperationException("Unsupported gameplay signature: " + typeName + "." + name);
            Patches.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(GameplayTelemetry), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(GameplayTelemetry), postfix));
        }

        // Attack.Start takes a Unity physics type this plugin never references, so the
        // overload is matched on shape instead of on a named parameter list.
        private static void PatchByShape(string typeName, string name, int parameters, Type result, string? prefix, string? postfix)
        {
            MethodInfo? match = null;
            foreach (MethodInfo candidate in Game(typeName).GetMethods(Declared))
            {
                if (candidate.Name != name || candidate.IsStatic || candidate.ReturnType != result) continue;
                if (candidate.GetParameters().Length != parameters) continue;
                if (match != null) throw new InvalidOperationException("Ambiguous gameplay shape: " + typeName + "." + name);
                match = candidate;
            }
            if (match == null) throw new InvalidOperationException("Unsupported gameplay shape: " + typeName + "." + name);
            Patches.Patch(match, prefix: prefix == null ? null : new HarmonyMethod(typeof(GameplayTelemetry), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(GameplayTelemetry), postfix));
        }

        private static Func<T, int> Accessor<T>(string name) =>
            AccessTools.MethodDelegate<Func<T, int>>(AccessTools.DeclaredMethod(typeof(T), name, Type.EmptyTypes)
                ?? throw new InvalidOperationException("Missing accessor: " + typeof(T).Name + "." + name));

        // Player derives from a Unity-interface type, so the reader is built through a
        // reflected open delegate over object rather than a typed field reference.
        private static Func<object, int> GhostReader()
        {
            FieldInfo field = AccessTools.Field(Game("Player"), "m_placementGhost")
                ?? throw new InvalidOperationException("Player.m_placementGhost is unavailable.");
            if (field.FieldType != typeof(GameObject)) throw new InvalidOperationException("Unexpected placement ghost type.");
            return instance => ((GameObject?)field.GetValue(instance)) == null ? 0 : 1;
        }

        private static Func<object, bool> ShipAttachmentReader()
        {
            MethodInfo method = AccessTools.DeclaredMethod(Game("Player"), "IsAttachedToShip", Type.EmptyTypes)
                ?? throw new InvalidOperationException("Player.IsAttachedToShip is unavailable.");
            if (method.ReturnType != typeof(bool) || method.IsStatic) throw new InvalidOperationException("Unexpected ship attachment shape.");
            return instance => (bool)method.Invoke(instance, null)!;
        }

        private static MemberCount Collection(string typeName, string member)
        {
            var result = new MemberCount();
            Type owner = Game(typeName);
            result.Property = owner.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            result.Field = result.Property != null ? null : owner.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Type? collection = result.Property?.PropertyType ?? result.Field?.FieldType;
            if (collection == null || collection.IsValueType)
                throw new InvalidOperationException("Uncountable instance list: " + typeName + "." + member);
            PropertyInfo? count = collection.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (count == null || count.PropertyType != typeof(int))
                throw new InvalidOperationException("Instance list has no int Count: " + typeName + "." + member);
            result.Count = count;
            return result;
        }

        private static bool Observe()
        {
            if (!Enabled) return false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread) { Interlocked.Increment(ref otherThreadSkips); return false; }
            return true;
        }

        // --- container and inventory ------------------------------------------------

        private static void BeforeRequestOpen(Container __instance)
        {
            if (!Observe()) return;
            try
            {
                containerRequestsReceived++;
                if (__instance.IsInUse()) containerConflicts++;
            }
            catch { probeFailures++; }
        }
        private static void BeforeOpenResponse(bool __1)
        {
            if (!Observe()) return;
            if (__1) containerGranted++; else containerRejectedInUse++;
        }
        private static void OnContainerChanged() { if (Observe()) containerChanges++; }
        private static void OnInventoryMove() { if (Observe()) inventoryMoves++; }
        private static void OnInventoryAdd() { if (Observe()) inventoryStacks++; }

        // --- stations ---------------------------------------------------------------

        private static void BeforeSmelter(Smelter __instance, out int __state)
        {
            __state = -1;
            if (!Observe()) return;
            try { __state = queueSize!(__instance) + processedQueueSize!(__instance); smelterUpdates++; }
            catch { probeFailures++; }
        }
        private static void AfterSmelter(Smelter __instance, int __state)
        {
            if (__state < 0 || !Observe()) return;
            try
            {
                int consumed = __state - (queueSize!(__instance) + processedQueueSize!(__instance));
                if (consumed <= 0) return;
                smelterCatchupSum += consumed;
                if (consumed > smelterCatchupMax) smelterCatchupMax = consumed;
            }
            catch { probeFailures++; }
        }
        private static void OnSmelterSpawn() { if (Observe()) smelterSpawns++; }
        private static void OnFireplaceFuel() { if (Observe()) fireplaceFuelAdds++; }
        private static void OnCookingSpawn() { if (Observe()) cookingSpawns++; }
        private static void OnBeehiveExtract() { if (Observe()) beehiveExtracts++; }

        // --- building ---------------------------------------------------------------

        private static void AfterPlacementGhost(object __instance)
        {
            if (!Observe()) return;
            try
            {
                placementGhostCalls++;
                if (placementGhost!(__instance) != 0) placementGhostFrames++;
            }
            catch { probeFailures++; }
        }
        private static void OnPiecePlaced() { if (Observe()) piecesPlaced++; }
        private static void AfterPieceRemoved(bool __result) { if (__result && Observe()) piecesRemoved++; }

        // Growth of the caller's two reused buffers across one snap-point scan.
        private struct SnapState { internal int Points, Pieces; }

        private static void BeforeSnapPoints(object __2, object __3, out SnapState __state)
        {
            __state = new SnapState { Points = -1, Pieces = -1 };
            if (!Observe()) return;
            __state = new SnapState { Points = Size(__2), Pieces = Size(__3) };
        }
        private static void AfterSnapPoints(object __2, object __3, SnapState __state)
        {
            if (__state.Points < 0 || __state.Pieces < 0 || !Observe()) return;
            int points = Size(__2) - __state.Points, pieces = Size(__3) - __state.Pieces;
            if (points > 0) snapPointsEnumerated += points;
            if (pieces > 0) snapPiecesScanned += pieces;
        }
        private static void AfterGhostClipping() { if (Observe()) ghostClippingTests++; }

        // --- clutter -----------------------------------------------------------------

        private static void AfterGenerateVegPatch(object __result)
        {
            if (!Observe() || __result == null) return;
            try
            {
                clutterPatchesGenerated++;
                clutterObjectsInstantiated += patchObjectCount!(__result);
            }
            catch { probeFailures++; }
        }
        private static void BeforeUpdateGrass(bool __1) { if (__1 && Observe()) clutterRebuildAllFrames++; }
        private static void AfterGroundInfo() { if (Observe()) clutterGroundQueries++; }
        private static void AfterHeightmapReady(bool __result) { if (!__result && Observe()) clutterHeightmapNotReadyFrames++; }
        private static void AfterTimeoutPatches(object __instance)
        {
            if (!Observe()) return;
            try { clutterPatchesTimedOut += timedOutPatchCount!(__instance); }
            catch { probeFailures++; }
        }

        // --- gathering and combat ---------------------------------------------------

        private static void OnTreeDamage() { if (Observe()) treeDamageRpcs++; }
        private static void OnTreeLogSpawn() { if (Observe()) treeLogsSpawned++; }
        private static void OnRockDamage() { if (Observe()) rockDamageRpcs++; }
        private static void AfterRockArea(bool __result) { if (__result && Observe()) rockAreaDestroys++; }
        private static void OnDestructibleDestroy() { if (Observe()) destructibleDestroys++; }
        private static void AfterAttackStart(bool __result) { if (__result && Observe()) attacksStarted++; }
        private static void OnCharacterDamage() { if (Observe()) hitsDealt++; }
        private static void OnDropEvent() { if (Observe()) dropEvents++; }
        private static void AfterDropList(List<GameObject> __result)
        {
            if (!Observe() || __result == null) return;
            dropsSpawned += __result.Count;
        }

        // --- map --------------------------------------------------------------------

        private static void OnExploreUpdate() { if (Observe()) exploreUpdates++; }
        private static void BeforeExplore()
        {
            if (!Observe()) return;
            exploreScans++;
            if (exploreDepth++ == 0) exploreChanged = false;
        }
        private static void AfterExplore()
        {
            if (exploreDepth <= 0 || --exploreDepth != 0) return;
            if (exploreChanged && Observe()) fogApplies++;
            exploreChanged = false;
        }
        private static void AfterExplorePixel(bool __result)
        {
            if (!__result) return;
            exploreChanged = true;
            if (Observe()) fogPixels++;
        }

        // --- items ------------------------------------------------------------------

        private static void OnAutoStack() { if (Observe()) autoStacks++; }
        private static void OnSlowUpdate() { if (Observe()) slowUpdates++; }

        // --- one bounded per-frame observation --------------------------------------

        // The only per-frame work: a handful of static reads with no allocation, taken
        // from a method the game already runs once per frame.
        private static void AfterHudUpdate()
        {
            if (!Observe()) return;
            try
            {
                observedFrames++;
                InventoryGui gui = InventoryGui.instance;
                if (InventoryGui.IsVisible() && gui != null)
                {
                    inventoryGuiFrames++;
                    if (shownContainer != null && shownContainer(gui) != null) containerGuiFrames++;
                }
                Minimap map = Minimap.instance;
                if (mapMode != null && map != null && mapMode(map) == Minimap.MapMode.Large) largeMapFrames++;
                var local = Player.m_localPlayer;
                if (attachedToShip != null && local != null && attachedToShip(local)) onShipFrames++;
            }
            catch { probeFailures++; }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            PollSizes();
            void Count(string name, ref long counter, string unit = "calls") =>
                gauges.Add(new NumberValue(name, Interlocked.Exchange(ref counter, 0), unit));

            Count("container_open_requests_received", ref containerRequestsReceived);
            Count("container_concurrent_open_conflicts", ref containerConflicts);
            Count("container_open_granted", ref containerGranted);
            Count("container_open_requests_rejected_in_use", ref containerRejectedInUse);
            Count("container_changes", ref containerChanges);
            Count("container_gui_open_frames", ref containerGuiFrames, "frames");
            Count("inventory_gui_open_frames", ref inventoryGuiFrames, "frames");
            Count("inventory_moves", ref inventoryMoves);
            Count("inventory_item_stacks", ref inventoryStacks);
            gauges.Add(new NumberValue("inventory_items_max", inventoryItemsMax, "items"));

            Count("smelter_updates", ref smelterUpdates);
            Count("smelter_catchup_items_sum", ref smelterCatchupSum, "items");
            gauges.Add(new NumberValue("smelter_catchup_items_max", Interlocked.Exchange(ref smelterCatchupMax, 0), "items"));
            Count("smelter_spawns", ref smelterSpawns);
            Count("fireplace_fuel_adds", ref fireplaceFuelAdds);
            Count("cooking_spawns", ref cookingSpawns);
            Count("beehive_extracts", ref beehiveExtracts);

            Count("placement_ghost_updates", ref placementGhostCalls);
            Count("placement_ghost_frames", ref placementGhostFrames, "frames");
            Count("pieces_placed", ref piecesPlaced, "pieces");
            Count("pieces_removed", ref piecesRemoved, "pieces");
            Count("snap_pieces_scanned", ref snapPiecesScanned, "pieces");
            Count("snap_points_enumerated", ref snapPointsEnumerated, "points");
            Count("ghost_clipping_tests", ref ghostClippingTests);
            gauges.Add(new NumberValue("placement_update_over_10ms",
                TimingHooks.DrainPlacementUpdateOver10Ms(), "calls"));

            Count("clutter_patches_generated", ref clutterPatchesGenerated, "patches");
            Count("clutter_rebuild_all_frames", ref clutterRebuildAllFrames, "frames");
            Count("clutter_ground_queries", ref clutterGroundQueries);
            Count("clutter_objects_instantiated", ref clutterObjectsInstantiated, "objects");
            Count("clutter_heightmap_not_ready_frames", ref clutterHeightmapNotReadyFrames, "frames");
            Count("clutter_patches_timed_out", ref clutterPatchesTimedOut, "patches");

            Count("tree_damage_rpcs", ref treeDamageRpcs);
            Count("tree_logs_spawned", ref treeLogsSpawned, "objects");
            Count("rock_damage_rpcs", ref rockDamageRpcs);
            Count("rock_area_destroys", ref rockAreaDestroys);
            Count("destructible_destroys", ref destructibleDestroys);
            Count("attacks_started", ref attacksStarted);
            Count("hits_dealt", ref hitsDealt);
            Count("drop_on_destroyed_events", ref dropEvents);
            Count("drops_spawned", ref dropsSpawned, "objects");

            Count("minimap_explore_updates", ref exploreUpdates);
            Count("minimap_explore_scans", ref exploreScans);
            Count("minimap_fog_applies", ref fogApplies);
            Count("minimap_fog_pixels_explored", ref fogPixels, "pixels");
            Count("minimap_large_map_frames", ref largeMapFrames, "frames");

            Count("gameplay_observed_frames", ref observedFrames, "frames");
            Count("player_on_ship_frames", ref onShipFrames, "frames");
            gauges.Add(new NumberValue("ship_instances_max", Interlocked.Exchange(ref shipMax, 0), "instances"));
            gauges.Add(new NumberValue("vagon_instances_max", Interlocked.Exchange(ref vagonMax, 0), "instances"));
            gauges.Add(new NumberValue("zsfx_instances_max", Interlocked.Exchange(ref sfxMax, 0), "instances"));

            Count("item_drops_autostacked", ref autoStacks);
            Count("item_drop_slow_updates", ref slowUpdates);

            Count("gameplay_other_thread_skips", ref otherThreadSkips);
            Count("gameplay_probe_failures", ref probeFailures);

            labels.Add(new TextValue("gameplay_telemetry_status", !Enabled ? "disabled" : Status));
            labels.Add(new TextValue("gameplay_scope",
                "counts_of_native_calls_and_poll_time_sizes; no_timing; frame_gauges_count_Hud_Update_frames_only; " +
                "conflicts_are_RPC_RequestOpen_arrivals_while_already_in_use; catchup_is_queue_drop_across_one_UpdateSmelter; " +
                "omitted_or_zero_counter_is_zero_observed_activity_not_complete_coverage"));
            labels.Add(new TextValue("gameplay_probes_unavailable",
                Missing.Count == 0 ? "none" : string.Join(",", Missing.ToArray())));
            labels.Add(new TextValue("gameplay_skipped_counters",
                "container_in_use_max:no_container_instance_list; smelter_instances_max:no_smelter_instance_list; " +
                "audio_sources_playing:no_cheap_bounded_read; " +
                "ghost_physics_queries:no_single_game_side_wrapper_for_the_ghost_overlap_and_raycast_calls; " +
                "ghost_clipping_pairs:pair_loop_is_inline_and_exits_early_so_only_the_call_count_is_observable"));
        }

        private static void PollSizes()
        {
            if (!Enabled) { inventoryItemsMax = 0; return; }
            // The local-player read is isolated: resolving it loads a game type whose
            // Unity interface a standalone CLR refuses, and that refusal arrives at the
            // call, not inside the callee.
            try { inventoryItemsMax = ReadInventorySize(); }
            catch { inventoryItemsMax = 0; Interlocked.Increment(ref probeFailures); }
            try
            {
                shipMax = Math.Max(shipMax, shipInstances.Read());
                vagonMax = Math.Max(vagonMax, vagonInstances.Read());
                sfxMax = Math.Max(sfxMax, sfxInstances.Read());
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static int ReadInventorySize()
        {
            var local = Player.m_localPlayer;
            Inventory? inventory = humanoidInventory == null || local == null ? null : humanoidInventory(local);
            return inventory == null ? 0 : inventory.NrOfItems();
        }

        internal static void Reset()
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            containerRequestsReceived = containerConflicts = containerGranted = containerRejectedInUse = 0;
            containerChanges = containerGuiFrames = inventoryGuiFrames = inventoryMoves = inventoryStacks = 0;
            smelterUpdates = smelterCatchupSum = smelterSpawns = fireplaceFuelAdds = cookingSpawns = beehiveExtracts = 0;
            placementGhostCalls = placementGhostFrames = piecesPlaced = piecesRemoved = 0;
            snapPiecesScanned = snapPointsEnumerated = ghostClippingTests = 0;
            clutterPatchesGenerated = clutterRebuildAllFrames = clutterGroundQueries = 0;
            clutterObjectsInstantiated = clutterHeightmapNotReadyFrames = clutterPatchesTimedOut = 0;
            // The build-mode stall bucket lives in the timing finalizer; drain it with the rest.
            TimingHooks.DrainPlacementUpdateOver10Ms();
            treeDamageRpcs = treeLogsSpawned = rockDamageRpcs = rockAreaDestroys = 0;
            destructibleDestroys = attacksStarted = hitsDealt = dropEvents = dropsSpawned = 0;
            exploreUpdates = exploreScans = fogApplies = fogPixels = largeMapFrames = 0;
            observedFrames = onShipFrames = autoStacks = slowUpdates = 0;
            otherThreadSkips = probeFailures = 0;
            inventoryItemsMax = smelterCatchupMax = shipMax = vagonMax = sfxMax = 0;
            exploreDepth = 0;
            exploreChanged = false;
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Reset();
            Release();
            Installed = false;
            Status = "disabled";
            placementGhost = null;
            attachedToShip = null;
            patchObjectCount = timedOutPatchCount = null;
            shipInstances = vagonInstances = sfxInstances = default;
            // Clearing the typed readers touches the same unloadable game types.
            try { ClearReaders(); }
            catch { Interlocked.Increment(ref probeFailures); }
            Missing.Clear();
        }

        private static void ClearReaders()
        {
            shownContainer = null;
            mapMode = null;
            humanoidInventory = null;
            queueSize = processedQueueSize = null;
        }

        // Harmony rescans every patched method when it removes a patch, so a shared
        // standalone CLR can refuse the removal for an unrelated Unity type.
        private static void Release()
        {
            try { Patches.UnpatchSelf(); }
            catch (Exception exception) { Status = Status + "_unpatch_failed_" + exception.GetType().Name; }
        }
    }
}
