using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

// Verifies the base-simulation probes against the installed client assembly.
// Heightmap and every other IMonoUpdater implementor cannot be type-loaded by a
// standalone CLR, so those contracts are proven through the guarded installer path
// instead of being reported as runtime-verified.
internal static class SimulationGameTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("SimulationGameTests: " + message);
        checks++;
    }

    private static string Describe(Type type) => type.IsGenericType
        ? type.Name + "<" + string.Join(",", type.GetGenericArguments().Select(Describe)) + ">"
        : type.FullName!;

    internal static void Run(Assembly game, Assembly plugin)
    {
        checks = 0;
        int unreadableBodies = 0;
        Type timing = plugin.GetType("BetterPerformance.TimingHooks", true)!;
        Type metricType = plugin.GetType("BetterPerformance.Core.Metric", true)!;
        var registered = (IDictionary)timing.GetField("Metrics", StaticPrivate)!.GetValue(null)!;
        var availability = ((IEnumerable)timing.GetField("Availability", StaticPrivate)!.GetValue(null)!)
            .Cast<object>().ToDictionary(
                v => (string)v.GetType().GetProperty("Name")!.GetValue(v)!,
                v => (string)v.GetType().GetProperty("Value")!.GetValue(v)!);

        string[] added = {
            "WearBatch", "WearSupportUpdate", "HeightmapLateBatch", "HeightmapRegenerate",
            "HeightmapApplyModifiers", "HeightmapCollisionRebuild", "HeightmapRenderRebuild",
            "TerrainCompApply", "PlantUpdate", "SmelterUpdate", "FireplaceUpdate",
            "CookingStationUpdate", "BeehiveUpdate", "SapCollectorUpdate", "FermenterUpdate",
            "WindmillUpdate", "LocationSpawn", "VegetationPlace", "ZoneSpawn",
            "ZonePlaceLocations", "DungeonGenerate", "DungeonSpawn"
        };
        foreach (string name in added) Check(Enum.IsDefined(metricType, name), name + ": metric is exportable");

        // Exact managed signatures the installer must have registered. Every entry here is
        // resolvable by a standalone CLR; Heightmap-dependent ones are handled separately.
        var contracts = new (string Type, string Method, string Metric, string[] Parameters, string Return, bool Static)[] {
            ("WearNTearUpdater", "UpdateWearNTear", "WearBatch", new[] { "System.Single", "System.Single" }, "System.Void", false),
            ("WearNTear", "UpdateSupport", "WearSupportUpdate", new string[0], "System.Void", false),
            ("Plant", "SUpdate", "PlantUpdate", new[] { "System.Single", "Vector2s" }, "System.Void", false),
            ("Smelter", "UpdateSmelter", "SmelterUpdate", new string[0], "System.Void", false),
            ("Fireplace", "UpdateFireplace", "FireplaceUpdate", new string[0], "System.Void", false),
            ("CookingStation", "UpdateCooking", "CookingStationUpdate", new string[0], "System.Void", false),
            ("Beehive", "UpdateBees", "BeehiveUpdate", new string[0], "System.Void", false),
            ("SapCollector", "UpdateTick", "SapCollectorUpdate", new string[0], "System.Void", false),
            ("Fermenter", "SlowUpdate", "FermenterUpdate", new string[0], "System.Void", false),
            ("Windmill", "Update", "WindmillUpdate", new string[0], "System.Void", false),
            ("ZoneSystem", "SpawnLocation", "LocationSpawn", new[] {
                "ZoneSystem+ZoneLocation", "System.Int32", "UnityEngine.Vector3", "UnityEngine.Quaternion",
                "ZoneSystem+SpawnMode", "List`1<UnityEngine.GameObject>", "System.Boolean" }, "UnityEngine.GameObject", false),
            ("ZoneSystem", "SpawnZone", "ZoneSpawn", new[] {
                "Vector2s", "ZoneSystem+SpawnMode", "UnityEngine.GameObject&" }, "System.Boolean", false),
            ("DungeonGenerator", "Generate", "DungeonGenerate", new[] { "ZoneSystem+SpawnMode" }, "System.Void", false),
            ("DungeonGenerator", "Spawn", "DungeonSpawn", new string[0], "System.Void", false)
        };
        foreach (var contract in contracts)
        {
            Type owner = game.GetType(contract.Type, true)!;
            var matches = owner.GetMethods(All).Where(m => m.Name == contract.Method &&
                m.GetParameters().Select(p => Describe(p.ParameterType)).SequenceEqual(contract.Parameters)).ToArray();
            Check(matches.Length == 1, contract.Metric + ": exactly one overload matches the expected parameter types");
            MethodInfo method = matches[0];
            Check(Describe(method.ReturnType) == contract.Return, contract.Metric + ": expected return type");
            Check(method.IsStatic == contract.Static, contract.Metric + ": expected static/instance shape");
            // A body whose locals reference an IMonoUpdater implementor cannot be read by a
            // standalone CLR. The exact registration below is the contract that matters.
            try { Check(method.GetMethodBody() != null, contract.Metric + ": current game ships a managed body"); }
            catch (TypeLoadException)
            {
                unreadableBodies++;
                Console.WriteLine("STATIC ONLY " + contract.Metric + ": exact signature and registration verified; " +
                    "offline CLR cannot read a body with Unity interface locals");
            }
            Check(registered.Contains(method) && registered[method]!.ToString() == contract.Metric,
                contract.Metric + ": production installer maps the exact game method to this metric");
        }

        // Population accessors: only List.Count is read, so each must be a static
        // parameterless method returning a collection.
        foreach (var (typeName, methodName, element) in new[] {
            ("WearNTear", "GetAllInstances", "WearNTear"),
            ("TerrainModifier", "GetAllInstances", "TerrainModifier"),
            ("SlowUpdate", "GetAllInstaces", "SlowUpdate") })
        {
            MethodInfo? accessor = game.GetType(typeName, true)!.GetMethod(methodName, All, null, Type.EmptyTypes, null);
            Check(accessor != null && accessor.IsStatic, typeName + "." + methodName + ": static parameterless accessor exists");
            Check(Describe(accessor!.ReturnType) == "List`1<" + element + ">", typeName + "." + methodName + ": returns the expected list");
            Check(typeof(ICollection).IsAssignableFrom(accessor.ReturnType), typeName + "." + methodName + ": Count is readable without enumerating");
        }

        // The wear budget is an instance field with no static accessor; the telemetry must
        // therefore export the compiled-in default only, never a live per-frame value.
        Type wearUpdater = game.GetType("WearNTearUpdater", true)!;
        FieldInfo? liveWear = wearUpdater.GetField("m_updatesPerFrame", All);
        FieldInfo? defaultWear = wearUpdater.GetField("c_UpdatesPerFrame", All);
        Check(liveWear != null && !liveWear.IsStatic && liveWear.FieldType == typeof(int),
            "WearNTearUpdater.m_updatesPerFrame is an instance field, so a live read is unavailable");
        Check(defaultWear != null && defaultWear.IsStatic && defaultWear.FieldType == typeof(int),
            "WearNTearUpdater.c_UpdatesPerFrame is a readable static default");
        FieldInfo? slowBudget = game.GetType("SlowUpdater", true)!.GetField("m_updatesPerFrame", All);
        Check(slowBudget != null && slowBudget.IsStatic && slowBudget.FieldType == typeof(int),
            "SlowUpdater.m_updatesPerFrame is a readable static field");

        // SlowUpdater.UpdateLoop is a coroutine factory. Timing it would measure the
        // IEnumerator allocation, not the work performed across frames, so it is not hooked.
        MethodInfo? slowLoop = game.GetType("SlowUpdater", true)!.GetMethod("UpdateLoop", All, null, Type.EmptyTypes, null);
        Check(slowLoop != null && typeof(IEnumerator).IsAssignableFrom(slowLoop.ReturnType),
            "SlowUpdater.UpdateLoop returns IEnumerator");
        Check(availability.TryGetValue("probe.SlowUpdateLoop", out var loopState) && loopState == "unavailable_coroutine",
            "installer reports the coroutine as unavailable with its reason (actual=" +
            (availability.TryGetValue("probe.SlowUpdateLoop", out var actualLoop) ? actualLoop : "missing") + ")");
        Check(!Enum.IsDefined(metricType, "SlowUpdateLoop"), "no metric was added for the skipped coroutine");

        // Generate(SpawnMode) is the outer entry game code calls; prove it delegates to
        // Generate(int, SpawnMode) rather than assuming it from body size.
        Type dungeon = game.GetType("DungeonGenerator", true)!;
        MethodInfo outer = dungeon.GetMethods(All).Single(m => m.Name == "Generate" && m.GetParameters().Length == 1);
        MethodInfo inner = dungeon.GetMethods(All).Single(m => m.Name == "Generate" && m.GetParameters().Length == 2);
        Check(CallsDirectly(outer, inner), "DungeonGenerate: the hooked one-argument entry calls the two-argument implementation");
        Check(registered.Contains(outer) && !registered.Contains(inner),
            "DungeonGenerate: only the outer entry is hooked, so the metric is not double counted");

        // The late-update batch filter compares against a literal the game actually ships.
        string batchName = (string)timing.GetField("HeightmapLateBatchName", BindingFlags.Static | BindingFlags.NonPublic |
            BindingFlags.Public | BindingFlags.FlattenHierarchy)!.GetRawConstantValue()!;
        Check(batchName == "MonoUpdaters.LateUpdate.Heightmap", "heightmap batch filter uses the documented profiler scope");
        Check(CountLiteral(game, batchName) == 1, "the batch literal occurs exactly once in the installed assembly");
        MethodInfo prefix = timing.GetMethod("HeightmapLatePrefix", StaticPrivate)!;
        ParameterInfo[] prefixParameters = prefix.GetParameters();
        Check(prefix.ReturnType == typeof(void) && prefixParameters.Length == 2,
            "heightmap batch prefix cannot skip the original method");
        Check(prefixParameters[0].Name == "__2" && prefixParameters[0].ParameterType == typeof(string),
            "heightmap batch prefix reads the profiler-scope string at argument index 2");
        Check(prefixParameters[1].IsOut, "heightmap batch prefix only produces timing state");
        Check(timing.GetMethod("HeightmapLateFinalizer", StaticPrivate)!.ReturnType == typeof(void),
            "heightmap batch finalizer cannot replace a result or exception");

        // Everything reached through IMonoUpdater is unverifiable in this process by
        // construction. Prove the guard, never report these as runtime-verified.
        bool heightmapUnloadable = false;
        try { game.GetType("Heightmap", false); }
        catch (TypeLoadException) { heightmapUnloadable = true; }
        Check(heightmapUnloadable, "standalone CLR cannot type-load Heightmap, so the installer must guard it");
        string[] guarded = {
            "HeightmapLateBatch", "HeightmapRegenerate", "HeightmapApplyModifiers",
            "HeightmapCollisionRebuild", "HeightmapRenderRebuild", "TerrainCompApply",
            "VegetationPlace", "ZonePlaceLocations"
        };
        foreach (string metric in guarded)
        {
            Check(availability.TryGetValue("probe." + metric, out var state), metric + ": installer reported a status");
            Check(availability["probe." + metric] != "enabled",
                metric + ": offline status must not claim a verified patch (actual=" + availability["probe." + metric] + ")");
            Check(!registered.Values.Cast<object>().Any(v => v.ToString() == metric),
                metric + ": no game method was registered while its type could not be loaded");
        }
        Console.WriteLine("STATIC ONLY " + guarded.Length + " heightmap/terrain probes: guarded installer path verified; " +
            "signatures require the Unity runtime and are not claimed here");

        // The gauge entry point the collector calls on the main thread.
        MethodInfo sample = plugin.GetType("BetterPerformance.SimulationPopulationTelemetry", true)!
            .GetMethod("Sample", StaticPrivate)!;
        Check(sample.ReturnType == typeof(void) && sample.GetParameters().Length == 2,
            "SimulationPopulationTelemetry.Sample is a void gauge collector");
        Check(Describe(sample.GetParameters()[0].ParameterType) == "List`1<BetterPerformance.Core.NumberValue>" &&
            Describe(sample.GetParameters()[1].ParameterType) == "List`1<BetterPerformance.Core.TextValue>",
            "SimulationPopulationTelemetry.Sample matches the gauge/label collector contract");

        Console.WriteLine("PASS " + checks + " base-simulation checks; bodies unreadable offline=" +
            unreadableBodies + "; no game method invoked");
    }

    private static bool CallsDirectly(MethodInfo caller, MethodInfo target)
    {
        byte[]? body = caller.GetMethodBody()?.GetILAsByteArray();
        if (body == null) return false;
        for (int i = 0; i + 4 < body.Length; i++)
        {
            if (body[i] != OpCodes.Call.Value && body[i] != OpCodes.Callvirt.Value) continue;
            try
            {
                if (caller.Module.ResolveMethod(BitConverter.ToInt32(body, i + 1)) == target) return true;
            }
            catch { }
        }
        return false;
    }

    private static int CountLiteral(Assembly game, string literal)
    {
        byte[] raw = File.ReadAllBytes(game.Location);
        byte[] pattern = Encoding.Unicode.GetBytes(literal);
        int hits = 0;
        for (int i = 0; i + pattern.Length <= raw.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++) if (raw[i + j] != pattern[j]) { match = false; break; }
            if (match) hits++;
        }
        return hits;
    }
}
