using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// DungeonGenerator reaches Unity types a standalone CLR cannot type-load, so every game
// signature, call order and IL check here is read from Mono.Cecil metadata. Reflection is
// used only for the plugin's own configuration, counters and status.
internal static class DungeonGameTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Dungeon spawn slicing: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);
        TypeDefinition generator = gameModule.GetType("DungeonGenerator")
            ?? throw new InvalidOperationException("Dungeon spawn slicing: DungeonGenerator is missing.");
        TypeDefinition snap = gameModule.GetType("SnapToGround")
            ?? throw new InvalidOperationException("Dungeon spawn slicing: SnapToGround is missing.");

        MethodDefinition Method(string name, params string[] parameterTypes) =>
            generator.Methods.SingleOrDefault(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes))
            ?? throw new InvalidOperationException("Dungeon spawn slicing: DungeonGenerator." + name +
                "(" + string.Join(", ", parameterTypes) + ") is missing.");

        // 1. The four generator members the module patches or replays.
        MethodDefinition spawn = Method("Spawn");
        Check(!spawn.IsStatic && spawn.IsPrivate && spawn.ReturnType.FullName == "System.Void" && spawn.HasBody,
            "Spawn() is a private instance void method");
        MethodDefinition placeRoom = Method("PlaceRoom", "DungeonDB/RoomData", "UnityEngine.Vector3",
            "UnityEngine.Quaternion", "RoomConnection", "ZoneSystem/SpawnMode");
        Check(!placeRoom.IsStatic && placeRoom.IsPrivate && placeRoom.ReturnType.FullName == "Room",
            "PlaceRoom(RoomData, Vector3, Quaternion, RoomConnection, SpawnMode) is a private instance method returning Room");
        MethodDefinition release = Method("ReleaseHeldReferences");
        Check(!release.IsStatic && release.IsPrivate && release.ReturnType.FullName == "System.Void",
            "ReleaseHeldReferences() is a private instance void method");
        MethodDefinition destroy = Method("OnDestroy");
        Check(destroy.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == release),
            "OnDestroy() still calls ReleaseHeldReferences, which is the escape the module relies on");
        MethodDefinition roomLoaded = generator.Methods.SingleOrDefault(m => m.Name == "OnRoomLoaded")
            ?? throw new InvalidOperationException("Dungeon spawn slicing: DungeonGenerator.OnRoomLoaded is missing.");
        Check(!roomLoaded.IsStatic && roomLoaded.IsPrivate && roomLoaded.Parameters.Count == 2 && roomLoaded.HasBody,
            "OnRoomLoaded is the private two-argument asset-load callback");

        // 2. The room array and its element layout the coroutine reads.
        FieldDefinition loadedRooms = generator.Fields.SingleOrDefault(f => f.Name == "m_loadedRooms")
            ?? throw new InvalidOperationException("Dungeon spawn slicing: m_loadedRooms is missing.");
        Check(!loadedRooms.IsStatic && loadedRooms.FieldType.IsArray, "m_loadedRooms is an instance array field");
        TypeDefinition element = loadedRooms.FieldType.GetElementType().Resolve();
        Check(element.IsValueType && element.DeclaringType == generator, "the room placement element is a nested value type");
        foreach (var (field, type) in new[] { ("m_roomData", "DungeonDB/RoomData"),
            ("m_position", "UnityEngine.Vector3"), ("m_rotation", "UnityEngine.Quaternion") })
            Check(element.Fields.Any(f => f.Name == field && !f.IsStatic && f.FieldType.FullName == type),
                "the room placement element still carries " + field + " as " + type);

        // 3. Spawn iterates m_loadedRooms placing rooms in Client mode, then snaps and clears.
        var body = spawn.Body.Instructions;
        Check(body.Any(i => (i.Operand as FieldReference)?.Resolve() == loadedRooms), "Spawn still iterates m_loadedRooms");
        int placeIndex = body.ToList().FindIndex(i => (i.Operand as MethodReference)?.Resolve() == placeRoom);
        Check(placeIndex >= 0, "Spawn still calls the five-argument PlaceRoom");
        Check(body.Count(i => (i.Operand as MethodReference)?.Resolve() == placeRoom) == 1,
            "Spawn calls PlaceRoom from exactly one loop body");
        Check(body.Take(placeIndex).Any(i => i.OpCode == Cil.OpCodes.Ldnull),
            "Spawn passes a null RoomConnection to PlaceRoom, as the coroutine does");
        Check(body.Take(placeIndex).Any(i => i.OpCode == Cil.OpCodes.Ldc_I4_1),
            "Spawn hardcodes SpawnMode.Client (1); the prefix therefore covers no Full or Ghost placement");
        MethodDefinition snappAll = snap.Methods.SingleOrDefault(m => m.Name == "SnappAll" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Dungeon spawn slicing: SnapToGround.SnappAll() is missing.");
        Check(snappAll.IsStatic && snappAll.IsPublic && snappAll.ReturnType.FullName == "System.Void",
            "SnapToGround.SnappAll() is a public static void method");
        var tail = body.ToList();
        int snapIndex = tail.FindIndex(i => (i.Operand as MethodReference)?.Resolve() == snappAll);
        int clearIndex = tail.FindLastIndex(i => (i.Operand as FieldReference)?.Resolve() == loadedRooms &&
            i.OpCode == Cil.OpCodes.Stfld);
        Check(snapIndex > placeIndex && clearIndex > snapIndex,
            "Spawn ends with SnappAll then clears m_loadedRooms, which is the tail the final slice reproduces");

        // 4. The hazard: OnRoomLoaded releases the held references right after Spawn returns.
        var callback = roomLoaded.Body.Instructions.ToList();
        int spawnCall = callback.FindIndex(i => (i.Operand as MethodReference)?.Resolve() == spawn);
        int releaseCall = callback.FindIndex(i => (i.Operand as MethodReference)?.Resolve() == release);
        Check(spawnCall >= 0 && releaseCall > spawnCall,
            "OnRoomLoaded calls Spawn and then ReleaseHeldReferences, so the release must be deferred while a slice is in flight");

        // 5. Zone readiness: the registration is taken before the async load and dropped only
        //    by ReleaseHeldReferences, so deferring that release holds it to the last slice.
        MethodDefinition loadAsync = Method("LoadRoomPrefabsAsync");
        Check(loadAsync.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "SetLoadingInZone"),
            "LoadRoomPrefabsAsync still registers the generator ZDO through SetLoadingInZone");
        Check(release.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "UnsetLoadingInZone"),
            "ReleaseHeldReferences still calls UnsetLoadingInZone");
        Check(generator.Methods.Where(m => m != release).All(m => !m.HasBody ||
                !m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "UnsetLoadingInZone")),
            "ReleaseHeldReferences is the only generator method that releases the loading-in-zone registration");
        Check(generator.Methods.Where(m => m != loadAsync).All(m => !m.HasBody ||
                !m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "SetLoadingInZone")),
            "LoadRoomPrefabsAsync is the only generator method that takes the registration");

        // 6. The Full and Ghost layout path is unreachable from the patched seam.
        Check(generator.Methods.Where(m => m.Name == "Generate").All(m =>
                !m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == spawn)),
            "no Generate overload calls Spawn, so the sliced path is the client replay only");
        Check(generator.Methods.Single(m => m.Name == "Generate" && m.Parameters.Count == 2).Body.Instructions
                .Any(i => (i.Operand as MethodReference)?.Name == "Save"),
            "Generate(int, SpawnMode) is still the layout-and-save path the prefix never reaches");
        Check(generator.Methods.Where(m => m != roomLoaded).All(m => !m.HasBody ||
                !m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == spawn)),
            "OnRoomLoaded is the only caller of Spawn");

        // 7. PlaceRoom still saves and restores the random state per room, so a frame
        //    boundary between two calls cannot change what any room contains.
        var place = placeRoom.Body.Instructions.ToList();
        Check(place.Any(i => (i.Operand as MethodReference)?.Name == "get_state" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "UnityEngine.Random"),
            "PlaceRoom still saves UnityEngine.Random.state");
        Check(place.Any(i => (i.Operand as MethodReference)?.Name == "set_state" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "UnityEngine.Random"),
            "PlaceRoom still restores UnityEngine.Random.state");
        Check(place.Any(i => (i.Operand as FieldReference)?.Name == "m_placedRooms"),
            "PlaceRoom still reaches m_placedRooms only on the non-client branch it guards with SpawnMode");

        // 8. The bounds fields the break-glass check rebuilds.
        foreach (string field in new[] { "m_zoneSize", "m_originalPosition" })
            Check(generator.Fields.Any(f => f.Name == field && f.IsPublic && !f.IsStatic &&
                    f.FieldType.FullName == "UnityEngine.Vector3"),
                field + " is a public instance Vector3 the bounds check reads");
        Check(generator.Methods.Single(m => m.Name == "Generate" && m.Parameters.Count == 2).Body.Instructions
                .Any(i => (i.Operand as FieldReference)?.Name == "m_zoneCenter"),
            "m_zoneCenter is assigned only by Generate, which no client runs, so the module recomputes the zone centre");

        // 9. The plugin patches: two skipping prefixes and one void escape.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.DungeonSpawnSlicing")!;
        foreach (string name in new[] { "BeforeSpawn", "BeforeRelease" })
        {
            MethodDefinition prefix = module.Methods.Single(m => m.Name == name);
            Check(prefix.IsStatic && prefix.ReturnType.FullName == "System.Boolean" && prefix.Parameters.Count == 1 &&
                prefix.Parameters[0].ParameterType.FullName == "DungeonGenerator",
                name + " is a static bool prefix taking the generator instance");
        }
        MethodDefinition escape = module.Methods.Single(m => m.Name == "BeforeDestroy");
        Check(escape.IsStatic && escape.ReturnType.FullName == "System.Void",
            "BeforeDestroy is a void prefix, so it can never skip the native OnDestroy");
        MethodDefinition finish = module.Methods.Single(m => m.Name == "Finish");
        var finishBody = finish.Body.Instructions.ToList();
        // Cross-module: the plugin's reference resolves through its own resolver, so it is
        // matched by name rather than by TypeDefinition identity.
        Check(finishBody.Any(i => (i.Operand as MethodReference)?.Name == snappAll.Name &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "SnapToGround"),
            "the final slice calls SnapToGround.SnappAll exactly as the native tail does");
        Check(finishBody.Any(i => (i.Operand as MethodReference)?.Name == "SetValue"),
            "the final slice clears m_loadedRooms before releasing the held references");
        Check(module.Methods.Single(m => m.Name == "Place").Body.Instructions
                .Any(i => i.OpCode == Cil.OpCodes.Ldc_I4_1),
            "the coroutine replays PlaceRoom in SpawnMode.Client");

        // 10. Default configuration installs nothing and patches nothing.
        Type reflected = plugin.GetType("BetterPerformance.DungeonSpawnSlicing", true)!;
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-dungeon-" + Guid.NewGuid().ToString("N") + ".cfg"), false)
        { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("DungeonVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        reflected.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        var option = (ConfigEntry<bool>)config[new ConfigDefinition("Dungeons", "SliceRoomSpawnEnabled")];
        Check(!option.Value && !(bool)option.DefaultValue, "SliceRoomSpawnEnabled defaults to false");
        var budget = (ConfigEntry<float>)config[new ConfigDefinition("Dungeons", "RoomSpawnBudgetMilliseconds")];
        Check((float)budget.DefaultValue == 4f, "RoomSpawnBudgetMilliseconds defaults to 4");
        var range = (AcceptableValueRange<float>)budget.Description.AcceptableValues;
        Check(range.MinValue == 1f && range.MaxValue == 50f, "RoomSpawnBudgetMilliseconds accepts 1 to 50");
        Check(!(bool)reflected.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "default configuration installs no patch");
        Check(!(bool)reflected.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "default configuration stays disabled");
        Check((string)reflected.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "default status reports disabled");
        Check((int)reflected.GetProperty("PendingCount", PrivateStatic)!.GetValue(null)! == 0, "default configuration queues no dungeon");
        Check(!(bool)reflected.GetMethod("SetEnabled", PrivateStatic)!.Invoke(null, new object[] { true })!,
            "the runtime toggle cannot enable a module that did not install");
        Check(Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
                .All(owner => !owner.EndsWith("DungeonSpawnSlicing")),
            "default configuration leaves every method unpatched by this module");

        // 11. Counters and labels exist whether or not the module installed.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        reflected.GetMethod("Sample", PrivateStatic)!.Invoke(null, new object[] { gauges, labels });
        var exported = gauges.Cast<object>().Select(g => (string)number.GetProperty("Name")!.GetValue(g)!).ToList();
        foreach (string name in new[] { "dungeon_spawn_sliced", "dungeon_spawn_vanilla_player_inside",
            "dungeon_spawn_vanilla_server_or_full", "dungeon_rooms_placed", "dungeon_slices", "dungeon_slice_ms_max",
            "dungeon_spawn_span_frames_max", "dungeon_spawn_aborted_destroyed", "dungeon_release_held", "dungeon_probe_failures" })
            Check(exported.Contains(name), name + " is exported");
        var labelNames = labels.Cast<object>().Select(l => (string)text.GetProperty("Name")!.GetValue(l)!).ToList();
        foreach (string name in new[] { "dungeon_slicing_status", "dungeon_slicing_enabled", "dungeon_slicing_budget_ms" })
            Check(labelNames.Contains(name), name + " is exported");

        reflected.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Console.WriteLine("Dungeon spawn slicing: " + checks + " static game-contract checks from metadata; the sliced crypt approach requires a disposable-world A/B.");
        return checks;
    }
}
