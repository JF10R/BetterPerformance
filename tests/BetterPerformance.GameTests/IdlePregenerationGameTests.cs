using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// ZNet reaches native socket interfaces a standalone CLR cannot type-load, so every game
// contract is read from Mono.Cecil metadata. Never calls SpawnZone or any game method.
internal static class IdlePregenerationGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Idle pre-generation: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        bool serverBuild = Path.GetFileName(Path.GetDirectoryName(managed)!) == "valheim_server_Data";
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var gameModule = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition Require(string name) => gameModule.GetType(name)
            ?? throw new InvalidOperationException("Idle pre-generation: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameters) =>
            type.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
            ?? throw new InvalidOperationException("Idle pre-generation: " + type.Name + "." + name + "(" + string.Join(", ", parameters) + ") is missing.");
        FieldDefinition Field(TypeDefinition type, string name) => type.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException("Idle pre-generation: " + type.FullName + "." + name + " is missing.");
        bool Calls(MethodDefinition method, string declaring, string name) => method.HasBody && method.Body.Instructions.Any(i =>
            (i.OpCode == Cil.OpCodes.Call || i.OpCode == Cil.OpCodes.Callvirt) && i.Operand is MethodReference called &&
            called.Name == name && called.DeclaringType.Name == declaring);

        TypeDefinition zoneSystem = Require("ZoneSystem"), net = Require("ZNet"), peer = Require("ZNetPeer");
        TypeDefinition spawnMode = zoneSystem.NestedTypes.Single(t => t.Name == "SpawnMode");
        TypeDefinition locationInstance = zoneSystem.NestedTypes.Single(t => t.Name == "LocationInstance");
        TypeDefinition zoneLocation = zoneSystem.NestedTypes.Single(t => t.Name == "ZoneLocation");

        // 1. Role: the plugin installs only where IsDedicated() is the constant true.
        MethodDefinition dedicated = Method(net, "IsDedicated");
        Check(!dedicated.IsStatic && dedicated.IsPublic && dedicated.ReturnType.FullName == "System.Boolean", "ZNet.IsDedicated() is a public instance bool");
        Type module = plugin.GetType("BetterPerformance.IdleZonePregeneration", true)!;
        MethodInfo constant = module.GetMethod("ConstantResult", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool? Constant(List<CodeInstruction> code) => (bool?)constant.Invoke(null, new object[] { code });
        Check(Constant(Translate(dedicated)) == serverBuild,
            "the live IsDedicated body reads as the constant " + serverBuild + " on this " + (serverBuild ? "server" : "client") + " build");
        Check(Constant(new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldc_I4_1), new CodeInstruction(OpCodes.Ret) }) == true &&
            Constant(new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ret) }) == false,
            "the role test reads both constant bodies");
        Check(Constant(new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, typeof(string).GetField("Empty")), new CodeInstruction(OpCodes.Ret) }) == null &&
            Constant(new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ldc_I4_1), new CodeInstruction(OpCodes.Ret) }) == null,
            "a computed or ambiguous body is refused, so the module does not install");

        // 2. The hook and the exact native call vanilla makes for a peer's ghost zones.
        MethodDefinition update = Method(zoneSystem, "Update");
        Check(!update.IsStatic && update.ReturnType.FullName == "System.Void", "ZoneSystem.Update() is the instance void the postfix follows");
        MethodDefinition spawn = Method(zoneSystem, "SpawnZone", "Vector2s", "ZoneSystem/SpawnMode", "UnityEngine.GameObject&");
        Check(!spawn.IsStatic && spawn.ReturnType.FullName == "System.Boolean" && spawn.Parameters[2].IsOut,
            "ZoneSystem.SpawnZone(Vector2s, SpawnMode, out GameObject) returns bool");
        MethodDefinition isGenerated = Method(zoneSystem, "IsZoneGenerated", "Vector2s");
        Check(!isGenerated.IsStatic && isGenerated.ReturnType.FullName == "System.Boolean", "ZoneSystem.IsZoneGenerated(Vector2s) returns bool");
        Check(spawnMode.Fields.Where(f => f.IsLiteral).Select(f => f.Name + "=" + f.Constant).SequenceEqual(new[] { "Full=0", "Client=1", "Ghost=2" }),
            "SpawnMode is still Full, Client, Ghost");
        MethodDefinition ghost = Method(zoneSystem, "CreateGhostZones", "UnityEngine.Vector3");
        Check(Calls(ghost, "ZoneSystem", "IsZoneGenerated") && Calls(ghost, "ZoneSystem", "SpawnZone") &&
            ghost.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldc_I4_2), "vanilla ghost generation is still !IsZoneGenerated && SpawnZone(zone, Ghost)");
        Check(Calls(update, "ZoneSystem", "CreateGhostZones") && Calls(update, "ZoneSystem", "get_LocationsGenerated"),
            "vanilla Update still gates ghost generation on LocationsGenerated");
        Check(Calls(spawn, "ZoneSystem", "SetZoneGenerated") && Calls(spawn, "ZoneSystem", "PlaceLocations") &&
            Calls(spawn, "ZoneSystem", "PlaceVegetation") && Calls(spawn, "HeightmapBuilder", "IsTerrainReady"),
            "SpawnZone still generates, marks the zone and returns false while terrain is not ready");

        // 3. Generated-zone and location bookkeeping read before each call.
        FieldDefinition generated = Field(zoneSystem, "m_generatedZones");
        Check(!generated.IsStatic && generated.FieldType.FullName == "System.Collections.Generic.HashSet`1<Vector2s>", "ZoneSystem.m_generatedZones is a HashSet<Vector2s>");
        FieldDefinition locations = Field(zoneSystem, "m_locationInstances");
        Check(!locations.IsStatic && locations.IsPublic &&
            locations.FieldType.FullName == "System.Collections.Generic.Dictionary`2<Vector2s,ZoneSystem/LocationInstance>",
            "ZoneSystem.m_locationInstances is a public Dictionary<Vector2s, LocationInstance>");
        Check(Field(locationInstance, "m_placed").FieldType.FullName == "System.Boolean" &&
            Field(locationInstance, "m_location").FieldType.FullName == "ZoneSystem/ZoneLocation" &&
            Field(zoneLocation, "m_unique").FieldType.FullName == "System.Boolean" && Field(zoneLocation, "m_unique").IsPublic,
            "LocationInstance.m_placed, m_location and ZoneLocation.m_unique keep their types");
        Check(Calls(Method(zoneSystem, "PlaceLocations", "Vector2s", "UnityEngine.Vector3", "UnityEngine.Transform", "Heightmap",
            "System.Collections.Generic.List`1<ZoneSystem/ClearArea>", "ZoneSystem/SpawnMode", "System.Collections.Generic.List`1<UnityEngine.GameObject>"),
            "ZoneSystem", "RemoveUnplacedLocations"), "placing a unique location still removes its other candidate sites (why those zones are skipped)");
        MethodDefinition locationsGenerated = Method(zoneSystem, "get_LocationsGenerated");
        Check(locationsGenerated.IsPublic && locationsGenerated.ReturnType.FullName == "System.Boolean", "ZoneSystem.LocationsGenerated has a public bool getter");
        MethodDefinition getZone = Method(zoneSystem, "GetZone", "UnityEngine.Vector3");
        Check(getZone.IsStatic && getZone.IsPublic && getZone.ReturnType.FullName == "Vector2s", "ZoneSystem.GetZone(Vector3) is a public static Vector2s");

        // 4. Idleness: the peer list holds a connection from socket accept, handshake included.
        Check(Field(net, "m_peers").FieldType.FullName == "System.Collections.Generic.List`1<ZNetPeer>", "ZNet.m_peers is a List<ZNetPeer>");
        MethodDefinition peers = Method(net, "GetPeers");
        Check(peers.IsPublic && peers.ReturnType.FullName == "System.Collections.Generic.List`1<ZNetPeer>" &&
            peers.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_peers"), "ZNet.GetPeers() returns m_peers itself");
        MethodDefinition connection = Method(net, "OnNewConnection", "ZNetPeer");
        Check(connection.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_peers") &&
            connection.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Add"),
            "OnNewConnection adds the peer before any handshake");
        Check(Calls(Method(net, "CheckForIncommingServerConnections"), "ZNet", "OnNewConnection"), "an accepted server socket goes straight to OnNewConnection");
        MethodDefinition saving = Method(net, "IsSaving");
        Check(saving.IsPublic && saving.ReturnType.FullName == "System.Boolean" &&
            saving.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_saveThread"), "ZNet.IsSaving() reads the save thread");
        MethodDefinition distance = Method(net, "GetSyncedSimulationDistance");
        Check(distance.IsPublic && distance.ReturnType.FullName == "SimulationDistance" &&
            Method(Require("SimulationDistance"), "get_TotalSimulationDistance").ReturnType.FullName == "System.Int32",
            "ZNet.GetSyncedSimulationDistance().TotalSimulationDistance is an int");
        Check(Calls(ghost, "SimulationDistance", "get_TotalSimulationDistance"), "vanilla ghost radius is still the total simulation distance");
        MethodDefinition worldGetter = Method(net, "get_World");
        Check(worldGetter.IsStatic && worldGetter.ReturnType.FullName == "World" && Field(Require("World"), "m_name").FieldType.FullName == "System.String",
            "ZNet.World and World.m_name identify the world");
        TypeDefinition generator = Require("WorldGenerator");
        Check(Method(generator, "get_instance").IsStatic && Method(generator, "GetSeed").ReturnType.FullName == "System.Int32", "WorldGenerator.instance.GetSeed() is an int");
        Check(Method(peer, "IsReady").ReturnType.FullName == "System.Boolean" && Method(peer, "GetRefPos").ReturnType.FullName == "UnityEngine.Vector3" &&
            Field(peer, "m_characterID").FieldType.FullName == "ZDOID" && Method(Require("ZDOID"), "IsNone").ReturnType.FullName == "System.Boolean",
            "ZNetPeer.IsReady, GetRefPos and m_characterID.IsNone sample activity");

        // 5. Plugin side: an observational postfix, and the documented configuration defaults.
        MethodInfo after = module.GetMethod("AfterZoneUpdate", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(after.ReturnType == typeof(void) && after.GetParameters().Length == 1 && after.GetParameters()[0].Name == "__instance" &&
            !after.GetParameters()[0].ParameterType.IsByRef, "the postfix cannot alter ZoneSystem.Update or its result");
        string file = Path.Combine(Path.GetTempPath(), "bp-idle-pregen-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            var config = new ConfigFile(file, false);
            module.GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { config, new BepInEx.Logging.ManualLogSource("test") });
            Check(Equals(Entry(config, "IdlePregenerationEnabled"), false) && Equals(Entry(config, "IdleRadiusZones"), 2) &&
                Equals(Entry(config, "MaxZonesPerRun"), 300) && Equals(Entry(config, "IdleStartDelaySeconds"), 10f) &&
                Equals(Entry(config, "MaxMillisecondsPerFrame"), 8f), "[ServerGeneration] defaults: off, 2 rings, 300 zones, 10 s, 8 ms");
            Check((string)module.GetProperty("Status", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! == "disabled" &&
                !(bool)module.GetProperty("Installed", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!, "disabled by default installs nothing");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is TypeLoadException)
        {
            Console.WriteLine("STATIC ONLY Idle pre-generation defaults: offline .NET Framework cannot JIT Install; metadata contracts verified.");
        }
        finally { try { File.Delete(file); } catch { } }
        Console.WriteLine("PASS Idle pre-generation: " + checks + " checks (" + (serverBuild ? "server" : "client") + " build); no game methods invoked");
        return checks;
    }

    private static object Entry(ConfigFile config, string key) =>
        config[new ConfigDefinition("ServerGeneration", key)].BoxedValue;

    // Cecil to reflection opcodes by name, enough for the constant-body test.
    private static List<CodeInstruction> Translate(MethodDefinition method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!).GroupBy(o => o.Name).ToDictionary(g => g.Key!, g => g.First());
        return method.Body.Instructions.Select(i => new CodeInstruction(opcodes[i.OpCode.Name], i.Operand is sbyte or int ? i.Operand : null)).ToList();
    }
}
