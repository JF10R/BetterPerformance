using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Game contracts behind TeleportLoadingTelemetry, the teleport unload in AssetUnloadDeferral and
// the loading-screen exemption in ObjectCreationBudget; Mono.Cecil metadata only, never calls the game.
internal static class TeleportGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Teleport loading: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var gameModule = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition Require(string name) => gameModule.GetType(name)
            ?? throw new InvalidOperationException("Teleport loading: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameters) =>
            type.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
            ?? throw new InvalidOperationException("Teleport loading: " + type.Name + "." + name + "(" + string.Join(", ", parameters) + ") is missing.");
        bool Calls(MethodDefinition method, string declaring, string name) => method.HasBody && method.Body.Instructions.Any(i =>
            (i.OpCode == Cil.OpCodes.Call || i.OpCode == Cil.OpCodes.Callvirt) && i.Operand is MethodReference called &&
            called.Name == name && called.DeclaringType.Name == declaring);
        bool LoadsFloat(MethodDefinition method, float value) => method.HasBody &&
            method.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == value);
        bool Stores(MethodDefinition method, string field) => method.HasBody &&
            method.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Stfld && i.Operand is FieldReference f && f.Name == field);
        void Field(TypeDefinition type, string name, string fullType) =>
            Check(type.Fields.Any(f => f.Name == name && !f.IsStatic && f.FieldType.FullName == fullType), type.Name + "." + name + " is an instance " + fullType);

        const string Vector3 = "UnityEngine.Vector3", Quaternion = "UnityEngine.Quaternion";
        TypeDefinition player = Require("Player"), scene = Require("ZNetScene"), zones = Require("ZoneSystem");

        // 1. The teleport state the probe and the unload read, and where the native loop sets it.
        Field(player, "m_teleporting", "System.Boolean");
        Field(player, "m_distantTeleport", "System.Boolean");
        Field(player, "m_teleportTimer", "System.Single");
        Field(player, "m_teleportTargetPos", Vector3);
        MethodDefinition teleportTo = Method(player, "TeleportTo", Vector3, Quaternion, "System.Boolean");
        Check(teleportTo.ReturnType.FullName == "System.Boolean" && Stores(teleportTo, "m_teleporting") && Stores(teleportTo, "m_teleportTargetPos"),
            "Player.TeleportTo(Vector3, Quaternion, bool) returns bool and starts the teleport");
        MethodDefinition update = Method(player, "UpdateTeleport", "System.Single");
        Check(!update.IsStatic && update.ReturnType.FullName == "System.Void", "Player.UpdateTeleport(float) is an instance void");
        Check(LoadsFloat(update, 2f) && LoadsFloat(update, 8f), "UpdateTeleport still moves at 2 s and holds a distant teleport to 8 s");
        Check(Calls(update, "ZNetScene", "IsAreaReady") && Calls(update, "ZoneSystem", "FindFloor") && Stores(update, "m_teleporting"),
            "UpdateTeleport still ends on IsAreaReady and FindFloor");
        Check(Calls(Method(player, "FixedUpdate"), "Player", "UpdateTeleport"), "Player.FixedUpdate still drives UpdateTeleport");

        // 2. The readiness checks the probe polls after the move.
        MethodDefinition ready = Method(scene, "IsAreaReady", Vector3);
        Check(ready.IsPublic && !ready.IsStatic && ready.ReturnType.FullName == "System.Boolean", "ZNetScene.IsAreaReady(Vector3) is a public bool");
        Check(Method(zones, "IsZoneLoaded", Vector3).IsPublic && Method(zones, "IsActiveAreaLoaded").IsPublic, "ZoneSystem zone checks are public");
        MethodDefinition floor = Method(zones, "FindFloor", Vector3, "System.Single&");
        Check(floor.IsPublic && floor.ReturnType.FullName == "System.Boolean", "ZoneSystem.FindFloor(Vector3, out float) is a public bool");
        Check(scene.Fields.Any(f => f.Name == "m_instances" && f.FieldType.FullName.StartsWith("System.Collections.Generic.Dictionary`2<ZDO,ZNetView>")),
            "ZNetScene.m_instances is a Dictionary<ZDO, ZNetView>");

        // 3. The loading screen the object budget now leaves to vanilla, and the unload it offers.
        MethodDefinition loading = Method(scene, "InLoadingScreen");
        // IsTeleporting is declared on Character and overridden by Player: the call site names Character.
        Check((Calls(loading, "Character", "IsTeleporting") || Calls(loading, "Player", "IsTeleporting")) &&
            loading.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_localPlayer"),
            "ZNetScene.InLoadingScreen() is still 'no local player or teleporting'");
        MethodDefinition create = Method(scene, "CreateObjects", "System.Collections.Generic.List`1<ZDO>", "System.Collections.Generic.List`1<ZDO>");
        Check(Calls(create, "ZNetScene", "InLoadingScreen") && create.Body.Instructions.Any(i => i.Operand is sbyte q && q == 100),
            "CreateObjects still raises its quota to 100 in a loading screen");
        MethodDefinition check = Method(Require("Game"), "CollectResourcesCheck");
        Check(check.IsPublic && !check.IsStatic, "Game.CollectResourcesCheck() is public");

        // 4. FastTeleportArrival: raising m_teleportTimer past 8 s must leave vanilla's own checks in place,
        // i.e. the 8 s comparison comes before IsAreaReady, itself before FindFloor.
        int IndexOf(MethodDefinition method, Func<Cil.Instruction, bool> match) => method.Body.Instructions.ToList().FindIndex(i => match(i));
        int eight = IndexOf(update, i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == 8f);
        int areaReady = IndexOf(update, i => i.Operand is MethodReference m && m.Name == "IsAreaReady");
        int findFloor = IndexOf(update, i => i.Operand is MethodReference m && m.Name == "FindFloor");
        Check(eight >= 0 && eight < areaReady && areaReady < findFloor, "UpdateTeleport tests 8 s, then IsAreaReady, then FindFloor");
        TypeDefinition clutter = Require("ClutterSystem"), heightmap = Require("Heightmap"), zdoman = Require("ZDOMan");
        Check(clutter.Fields.Any(f => f.Name == "m_patches" && f.FieldType.FullName.StartsWith("System.Collections.Generic.Dictionary`2<UnityEngine.Vector2Int,")),
            "ClutterSystem.m_patches is keyed by Vector2Int");
        Check(clutter.Fields.Any(f => f.Name == "m_forceRebuild" && !f.IsStatic && f.FieldType.FullName == "System.Boolean") &&
            Method(clutter, "LateUpdate").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_forceRebuild"),
            "ClutterSystem.LateUpdate still rebuilds all patches when m_forceRebuild is set");
        Check(clutter.Fields.Any(f => f.Name == "m_quality") && clutter.Fields.Any(f => f.Name == "m_distance" && f.IsPublic) &&
            clutter.Fields.Any(f => f.Name == "m_grassPatchSize" && f.IsPublic), "ClutterSystem quality, distance and patch size are readable");
        Check(Method(clutter, "GetVegPatch", Vector3).IsPublic && Method(clutter, "GetVegPatchCenter", "UnityEngine.Vector2Int").IsPublic,
            "ClutterSystem patch coordinates are public");
        MethodDefinition generate = Method(clutter, "GeneratePatches", "System.Boolean", Vector3);
        Check(Calls(generate, "Mathf", "CeilToInt") && Calls(generate, "ClutterSystem", "GetVegPatch"), "GeneratePatches still covers ceil((distance - size/2) / size) rings");
        Check(heightmap.Fields.Any(f => f.Name == "m_doLateUpdate" && f.IsPublic && f.FieldType.FullName == "System.Int32"), "Heightmap.m_doLateUpdate is a public int");
        MethodDefinition findHeightmaps = Method(heightmap, "FindHeightmap", Vector3, "System.Single", "System.Collections.Generic.List`1<Heightmap>");
        Check(findHeightmaps.IsPublic && findHeightmaps.IsStatic, "Heightmap.FindHeightmap(Vector3, float, List) is public static");
        Check(Method(zdoman, "FindSectorObjects", "Vector2s", "SimulationDistance", "System.Collections.Generic.List`1<ZDO>", "System.Collections.Generic.List`1<ZDO>").IsPublic,
            "ZDOMan.FindSectorObjects is public");
        Check(Require("SimulationDistance").Methods.Any(m => m.IsConstructor && m.IsPublic && m.Parameters.Count == 3), "SimulationDistance(int, int, bool) is public");
        Check(Calls(ready, "ZDOMan", "FindSectorObjects"), "IsAreaReady still walks FindSectorObjects");

        // 5. TeleportZonePreparation: the native terrain queue it feeds and the zone pacing the burst lifts.
        TypeDefinition builder = Require("HeightmapBuilder");
        MethodDefinition terrainReady = Method(builder, "IsTerrainReady", Vector3, "System.Int32", "System.Single", "System.Boolean", "WorldGenerator");
        Check(terrainReady.IsPublic && !terrainReady.IsStatic, "HeightmapBuilder.IsTerrainReady(...) is public: the call SpawnZone makes");
        Check(builder.Fields.Any(f => f.Name == "m_maxReadyQueue" && f.HasConstant && (int)f.Constant == 16), "HeightmapBuilder keeps 16 ready builds (MaxPrefetch)");
        Check(zones.Fields.Any(f => f.Name == "m_zonePrefab" && f.IsPublic), "ZoneSystem.m_zonePrefab is public");
        MethodDefinition zoneUpdate = Method(zones, "Update");
        Check(Calls(zoneUpdate, "ZoneSystem", "CreateLocalZones") && LoadsFloat(zoneUpdate, 0.1f), "ZoneSystem.Update still creates local zones on a 0.1 s timer");
        Check(Calls(Method(zones, "SpawnZone", "Vector2s", "ZoneSystem/SpawnMode", "UnityEngine.GameObject&"), "HeightmapBuilder", "IsTerrainReady"),
            "SpawnZone still waits for the builder's terrain");

        // 6. The pure rules the patches apply.
        Type timeline = plugin.GetType("BetterPerformance.Core.TeleportTimeline", true)!;
        Check(timeline.GetProperty("ReadyWaitMs") != null, "TeleportTimeline exposes the wait after readiness");
        Type policy = plugin.GetType("BetterPerformance.Core.AssetUnloadPolicy", true)!;
        MethodInfo offer = policy.GetMethod("OfferDuringTeleport", BindingFlags.Public | BindingFlags.Static)!;
        Check((bool)offer.Invoke(null, new object[] { true, 3.0, true, false })! && !(bool)offer.Invoke(null, new object[] { true, 1.0, true, false })!,
            "the teleport unload waits for the move and the ready area");
        Type arrival = plugin.GetType("BetterPerformance.Core.TeleportArrivalPolicy", true)!;
        Check(arrival.GetField("NativeFloorSeconds")?.GetValue(null) is double floorSeconds && floorSeconds == 8.0, "the policy's floor matches UpdateTeleport's 8 s");
        return checks;
    }
}
